using System.Diagnostics;
using System.Numerics;

namespace GPUTest.App.Simulation;

internal sealed class PhysicsSnapshot
{
    public BallPose[] Poses { get; set; } = [];
    public Vector3 CubePosition { get; set; }
    public Quaternion CubeOrientation { get; set; } = Quaternion.Identity;
    public int Generation { get; set; }
    public long Steps { get; set; }
    public bool Paused { get; set; }
    public bool SlowingDown { get; set; }
    public long AdapterLuid { get; set; }
}

internal sealed class PhysicsWorker : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<int, IPhysicsWorld> createWorld;
    private readonly Action? beforeStep;
    private PhysicsSnapshot exchange = new();
    private bool snapshotPending;
    private int count;
    private int generation = 1;
    private int clockVersion;
    private int requestedSteps;
    private bool paused;
    private bool suspended;
    private bool stopping;
    private Vector3 targetPosition;
    private Quaternion targetOrientation = Quaternion.Identity;
    private Exception? failure;

    public Exception? Failure => Volatile.Read(ref failure);

    public PhysicsWorker(int count, bool initiallyPaused = false, Func<int, IPhysicsWorld>? createWorld = null, Action? beforeStep = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 100_000);
        this.count = count;
        paused = initiallyPaused;
        this.createWorld = createWorld ?? (ballCount => new PhysicsWorld(ballCount));
        this.beforeStep = beforeStep;
        new Thread(Run) { IsBackground = true, Name = "GPU physics owner" }.Start();
    }

    public int Reset(int ballCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ballCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ballCount, 100_000);
        lock (gate)
        {
            if (stopping) return generation;
            count = ballCount;
            generation++;
            clockVersion++;
            requestedSteps = 0;
            paused = false;
            targetPosition = Vector3.Zero;
            targetOrientation = Quaternion.Identity;
            wake.Set();
            return generation;
        }
    }

    public void SetTarget(Vector3 position, Quaternion orientation)
    {
        lock (gate)
        {
            if (stopping) return;
            targetPosition = position;
            targetOrientation = orientation;
            wake.Set();
        }
    }

    public void SetPaused(bool value)
    {
        lock (gate)
        {
            if (stopping) return;
            paused = value;
            requestedSteps = 0;
            clockVersion++;
            wake.Set();
        }
    }

    public void SetSuspended(bool value)
    {
        lock (gate)
        {
            if (stopping || suspended == value) return;
            suspended = value;
            clockVersion++;
            wake.Set();
        }
    }

    public void RequestStep()
    {
        lock (gate)
        {
            if (stopping) return;
            paused = true;
            requestedSteps = checked(requestedSteps + 1);
            clockVersion++;
            wake.Set();
        }
    }

    public bool TrySwapSnapshot(ref PhysicsSnapshot snapshot)
    {
        lock (gate)
        {
            if (!snapshotPending) return false;
            (snapshot, exchange) = (exchange, snapshot);
            snapshotPending = false;
            return true;
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (!stopping) { stopping = true; wake.Set(); }
        }
        return completion.Task;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void Run()
    {
        IPhysicsWorld? world = null;
        var cube = new CubeController();
        var clock = new SimulationClock();
        var writing = new PhysicsSnapshot();
        int activeGeneration = 0;
        int activeClockVersion = -1;
        long steps = 0;
        long lastTick = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                int nextCount, nextGeneration, nextClockVersion;
                bool nextPaused, nextSuspended, singleStep;
                lock (gate)
                {
                    if (stopping) break;
                    nextCount = count;
                    nextGeneration = generation;
                    nextClockVersion = clockVersion;
                    nextPaused = paused;
                    nextSuspended = suspended;
                    singleStep = requestedSteps > 0 && activeGeneration == generation && !suspended;
                    cube.TargetPosition = targetPosition;
                    cube.TargetOrientation = targetOrientation;
                }

                bool reset = nextGeneration != activeGeneration;
                if (reset)
                {
                    world?.Dispose();
                    world = null;
                    world = createWorld(nextCount);
                    cube = new CubeController();
                    activeGeneration = nextGeneration;
                }
                bool clockChanged = reset || activeClockVersion != nextClockVersion;
                if (clockChanged)
                {
                    clock.Reset();
                    lastTick = Stopwatch.GetTimestamp();
                    activeClockVersion = nextClockVersion;
                }
                long now = Stopwatch.GetTimestamp();
                clock.BeginFrame(nextPaused || nextSuspended ? 0 : Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds);
                lastTick = now;
                bool advanced = false;
                long workStart = Stopwatch.GetTimestamp();
                while (singleStep || (!nextPaused && !nextSuspended && clock.TryTakeStep(Stopwatch.GetElapsedTime(workStart).TotalSeconds)))
                {
                    lock (gate)
                    {
                        if (stopping || generation != activeGeneration || clockVersion != activeClockVersion) break;
                        if (singleStep) requestedSteps--;
                        cube.TargetPosition = targetPosition;
                        cube.TargetOrientation = targetOrientation;
                    }
                    beforeStep?.Invoke();
                    cube.Advance();
                    world!.Step(cube.Position, cube.Orientation);
                    steps++;
                    advanced = true;
                    if (singleStep) break;
                }
                clock.EndFrame();
                if (advanced) world!.CopyPoses();
                if (world!.Escaped != 0) throw new InvalidOperationException($"Containment failure: {world.Escaped} escaped balls.");
                if (reset || advanced || clockChanged)
                {
                    if (writing.Poses.Length != world.Poses.Length) writing.Poses = new BallPose[world.Poses.Length];
                    world.Poses.CopyTo(writing.Poses, 0);
                    writing.CubePosition = cube.Position;
                    writing.CubeOrientation = cube.Orientation;
                    writing.Generation = activeGeneration;
                    writing.AdapterLuid = world.AdapterLuid;
                    writing.Steps = steps;
                    writing.Paused = nextPaused;
                    writing.SlowingDown = clock.IsSlowingDown
                        || (!nextPaused && advanced && Stopwatch.GetElapsedTime(workStart).TotalSeconds > PhysicsWorld.Timestep);
                    lock (gate)
                    {
                        (writing, exchange) = (exchange, writing);
                        snapshotPending = true;
                    }
                }
                if (!advanced)
                    wake.WaitOne(nextPaused || nextSuspended ? Timeout.Infinite
                        : Math.Max(1, (int)Math.Ceiling((PhysicsWorld.Timestep - clock.PendingSeconds) * 1000)));
            }
        }
        catch (Exception exception) { Volatile.Write(ref failure, exception); }
        finally
        {
            try { world?.Dispose(); }
            catch (Exception exception) { Volatile.Write(ref failure, exception); }
            lock (gate) stopping = true;
            wake.Dispose();
            completion.SetResult();
        }
    }
}