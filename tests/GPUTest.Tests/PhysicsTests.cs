using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GPUTest.App;
using GPUTest.App.Simulation;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GPUTest.Tests;

public sealed class PhysicsTests
{
    [Fact]
    public async Task WorkerAcceptsCommandsDuringBlockedStepAndKeepsSnapshotStable()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fake = new TestPhysicsWorld(1, entered, release);
        await using var worker = new PhysicsWorker(1, true, _ => fake);
        var snapshot = await NextSnapshot(worker, value => value.Paused);
        PhysicsSnapshot held = snapshot;
        worker.RequestStep();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            long start = Stopwatch.GetTimestamp();
            worker.SetTarget(Vector3.UnitX, Quaternion.Identity);
            worker.SetPaused(true);
            Assert.False(worker.TrySwapSnapshot(ref snapshot));
            Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(100));
            Assert.Equal(0, held.Steps);
            Assert.Equal(Vector3.Zero, held.Poses[0].Position);
        }
        finally { release.Set(); }
        snapshot = await NextSnapshot(worker, value => value.Steps == 1 && value.Paused, snapshot);
        Assert.Equal(Vector3.UnitX, snapshot.Poses[0].Position);
        worker.RequestStep();
        snapshot = await NextSnapshot(worker, value => value.Steps == 2, snapshot);
        Assert.Equal(new Vector3(2, 0, 0), snapshot.Poses[0].Position);
        await worker.StopAsync();
        Assert.Null(worker.Failure);
        Assert.True(fake.Disposed);
        Assert.Single(fake.Threads.Distinct());
    }

    [Fact]
    public async Task WorkerShutdownDoesNotDisposeAnInFlightWorld()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fake = new TestPhysicsWorld(1, entered, release);
        await using var worker = new PhysicsWorker(1, true, _ => fake);
        await NextSnapshot(worker, value => value.Paused);
        worker.RequestStep();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task stopped;
        try
        {
            stopped = worker.StopAsync();
            Assert.False(stopped.IsCompleted);
            Assert.False(fake.Disposed);
        }
        finally { release.Set(); }
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(worker.Failure);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task WorkerProcessesEveryQueuedSingleStep()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fake = new TestPhysicsWorld(1, entered, release);
        await using var worker = new PhysicsWorker(1, true, _ => fake);
        var snapshot = await NextSnapshot(worker, value => value.Paused);
        worker.RequestStep();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            for (int request = 0; request < 16; request++) worker.RequestStep();
        }
        finally { release.Set(); }
        snapshot = await NextSnapshot(worker, value => value.Steps == 17, snapshot);
        Assert.True(snapshot.Paused);
        Assert.Equal(new Vector3(17, 0, 0), snapshot.Poses[0].Position);
    }

    [Fact]
    public async Task WorkerResetReplacesWorldAndPublishesMatchingGeneration()
    {
        var worlds = new List<TestPhysicsWorld>();
        await using var worker = new PhysicsWorker(1, true, size =>
        {
            var world = new TestPhysicsWorld(size);
            worlds.Add(world);
            return world;
        });
        var snapshot = await NextSnapshot(worker, value => value.Generation == 1);
        Assert.Equal(worlds[0].AdapterLuid, snapshot.AdapterLuid);
        int generation = worker.Reset(2);
        worker.SetPaused(true);
        snapshot = await NextSnapshot(worker, value => value.Generation == generation && value.Paused, snapshot);
        Assert.Equal(2, snapshot.Poses.Length);
        Assert.Equal(worlds[1].AdapterLuid, snapshot.AdapterLuid);
        Assert.Equal(Vector3.Zero, snapshot.CubePosition);
        worker.RequestStep();
        snapshot = await NextSnapshot(worker, value => value.Generation == generation && value.Steps == 1, snapshot);
        await worker.StopAsync();
        Assert.Null(worker.Failure);
        Assert.All(worlds, world => Assert.True(world.Disposed));
        Assert.Single(worlds.SelectMany(world => world.Threads).Distinct());
    }

    [Fact]
    public async Task WorkerReportsNativeFailureAndStillDisposesWorld()
    {
        var fake = new TestPhysicsWorld(1) { FailStep = true };
        await using var worker = new PhysicsWorker(1, true, _ => fake);
        await NextSnapshot(worker, value => value.Paused);
        worker.RequestStep();
        long start = Stopwatch.GetTimestamp();
        while (worker.Failure is null && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.IsType<InvalidOperationException>(worker.Failure);
        await worker.StopAsync();
        Assert.True(fake.Disposed);
    }

    private static async Task<PhysicsSnapshot> NextSnapshot(PhysicsWorker worker, Func<PhysicsSnapshot, bool> predicate, PhysicsSnapshot? snapshot = null)
    {
        snapshot ??= new PhysicsSnapshot();
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10))
        {
            Assert.Null(worker.Failure);
            if (worker.TrySwapSnapshot(ref snapshot) && predicate(snapshot)) return snapshot;
            await Task.Delay(5);
        }
        throw new TimeoutException("Physics worker did not publish the expected snapshot.");
    }

    private sealed class TestPhysicsWorld(int count, ManualResetEventSlim? entered = null, ManualResetEventSlim? release = null) : IPhysicsWorld
    {
        public BallPose[] Poses { get; } = new BallPose[count];
        public int Escaped => 0;
        public long AdapterLuid => 100 + count;
        public List<int> Threads { get; } = [];
        public bool Disposed { get; private set; }
        public bool FailStep { get; init; }

        public void Step(Vector3 position, Quaternion orientation)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            entered?.Set();
            if (release is not null && !release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Blocked test step was not released.");
            if (FailStep) throw new InvalidOperationException("Injected physics failure.");
            if (Poses.Length > 0) Poses[0].Position += Vector3.UnitX;
        }

        public void CopyPoses() => Threads.Add(Environment.CurrentManagedThreadId);
        public void Dispose() { Threads.Add(Environment.CurrentManagedThreadId); Disposed = true; }
    }

    [Fact]
    public void SimulationClockContinuesAfterOverloadAndRecovers()
    {
        var clock = new SimulationClock();
        clock.BeginFrame(0.75);
        Assert.True(clock.TryTakeStep(0));
        Assert.False(clock.TryTakeStep(0.1));
        clock.EndFrame();
        Assert.True(clock.IsSlowingDown);
        Assert.InRange(clock.PendingSeconds, 0, PhysicsWorld.Timestep);

        clock.BeginFrame(PhysicsWorld.Timestep);
        Assert.True(clock.TryTakeStep(0));
        Assert.False(clock.TryTakeStep(0.001));
        clock.EndFrame();
        Assert.False(clock.IsSlowingDown);
    }

    [Fact]
    public void SimulationClockPreservesNormalFixedStepsAndFraction()
    {
        var clock = new SimulationClock();
        clock.BeginFrame(PhysicsWorld.Timestep * 2.5);
        Assert.True(clock.TryTakeStep(0));
        Assert.True(clock.TryTakeStep(0.003));
        Assert.False(clock.TryTakeStep(0.006));
        clock.EndFrame();
        Assert.False(clock.IsSlowingDown);
        Assert.Equal(PhysicsWorld.Timestep * 0.5, clock.PendingSeconds, 8);
        clock.BeginFrame(PhysicsWorld.Timestep * 0.5);
        Assert.True(clock.TryTakeStep(0));
        clock.EndFrame();
        Assert.Equal(0, clock.PendingSeconds);
    }

    [Fact]
    public void SimulationClockBoundsCatchUpAndResetsOnPause()
    {
        var clock = new SimulationClock();
        clock.BeginFrame(1);
        for (int step = 0; step < SimulationClock.MaximumStepsPerFrame; step++)
            Assert.True(clock.TryTakeStep(0));
        Assert.False(clock.TryTakeStep(0));
        clock.EndFrame();
        Assert.True(clock.IsSlowingDown);
        clock.Reset();
        Assert.False(clock.IsSlowingDown);
        Assert.Equal(0, clock.PendingSeconds);
        Assert.False(clock.TryTakeStep(0));
    }

    [Fact]
    public void PoseLayoutMatchesNativeAbi()
    {
        Assert.Equal(32, Marshal.SizeOf<BallPose>());
        Assert.Equal(12, Marshal.OffsetOf<BallPose>(nameof(BallPose.Radius)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<BallPose>(nameof(BallPose.Orientation)).ToInt32());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100001)]
    public void InvalidBallCountsAreRejected(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhysicsWorld(count));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    public void InvalidWorkerCountsAreRejected(int workers)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhysicsWorld(0, workers));
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void SceneQueryParticipationDoesNotChangePhysicalTrajectory()
    {
        var reference = new Vector3[120];
        for (int run = 0; run < 2; run++)
        {
            using var world = new PhysicsWorld(1, sceneQueries: run == 0);
            for (int step = 0; step < reference.Length; step++)
            {
                float time = step * PhysicsWorld.Timestep;
                world.Step(new Vector3(0.3f * MathF.Sin(time), 0.1f * MathF.Sin(time), 0),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f * MathF.Sin(time)));
                world.CopyPoses();
                Assert.Equal(0, world.Escaped);
                if (run == 0) reference[step] = world.Poses[0].Position;
                else Assert.InRange(Vector3.Distance(reference[step], world.Poses[0].Position), 0, 0.00001f);
            }
        }
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void ProfilingCanStopAndRestartWithoutChangingSimulation()
    {
        using var world = new PhysicsWorld(0);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            world.BeginProfile();
            world.Step(Vector3.Zero, Quaternion.Identity);
            world.ReportProfile(1);
            world.CopyPoses();
            Assert.Equal(0, world.Escaped);
        }
    }

    [Fact]
    public void CubeControllerAppliesLatestPointerTargetOnNextTick()
    {
        var controller = new CubeController();
        foreach (float direction in new[] { 1f, -1f, 0f })
        {
            controller.TargetPosition = new Vector3(direction * 2, direction * 0.5f, 0);
            controller.TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, direction * 0.6f);
            controller.Advance();
            Assert.Equal(controller.TargetPosition, controller.Position);
            Assert.True(MathF.Abs(Quaternion.Dot(controller.Orientation, controller.TargetOrientation)) > 0.99999f);
            controller.Advance();
            Assert.Equal(controller.TargetPosition, controller.Position);
        }
    }

    [Fact]
    public void CubeControllerUsesNewestTargetAndNormalizesRotation()
    {
        var controller = new CubeController();
        for (int update = 0; update < 50; update++)
        {
            controller.TargetPosition = new Vector3(update * 0.05f, 0, 0);
            controller.TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, update * 0.02f) * 2;
        }
        controller.Advance();
        Assert.Equal(controller.TargetPosition, controller.Position);
        Assert.InRange(controller.Orientation.Length(), 0.99999f, 1.00001f);
        Assert.True(MathF.Abs(Quaternion.Dot(controller.Orientation, Quaternion.Normalize(controller.TargetOrientation))) > 0.99999f);
    }

    [Fact]
    public void DenseSceneControllerRetainsMotionSafetyLimits()
    {
        var controller = new CubeController(directTracking: false)
        {
            TargetPosition = new Vector3(2, 0, 0),
            TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1)
        };
        for (int step = 0; step < 360; step++)
        {
            Vector3 previous = controller.Position;
            Quaternion rotation = controller.Orientation;
            controller.Advance();
            Assert.InRange(Vector3.Distance(previous, controller.Position), 0, CubeController.MaximumLinearSpeed * PhysicsWorld.Timestep + 0.0001f);
            float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(rotation, controller.Orientation)), 0, 1));
            Assert.InRange(angle, 0, CubeController.MaximumAngularSpeed * PhysicsWorld.Timestep + 0.001f);
        }
        Assert.Equal(controller.TargetPosition, controller.Position);
        Assert.True(MathF.Abs(Quaternion.Dot(controller.Orientation, controller.TargetOrientation)) > 0.99999f);
    }

    [Fact]
    public void CubeControllerTracksQuickDragWithoutLongTail()
    {
        var controller = new CubeController();
        for (int step = 0; step < 96; step++)
        {
            float direction = step < 48 ? 1 : -1;
            controller.TargetPosition += Vector3.UnitX * (direction * 6 * PhysicsWorld.Timestep);
            controller.TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (step + 1) * PhysicsWorld.Timestep);
            controller.Advance();
            Assert.InRange(Vector3.Distance(controller.Position, controller.TargetPosition), 0, 0.85f);
            Assert.True(MathF.Abs(Quaternion.Dot(controller.Orientation, controller.TargetOrientation)) > 0.9999f);
        }
        for (int step = 0; step < 12; step++) controller.Advance();
        Assert.InRange(Vector3.Distance(controller.Position, controller.TargetPosition), 0, 0.001f);
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void StationaryCubeSleepsAndWakesForNextTarget()
    {
        using var world = new PhysicsWorld(1);
        for (int step = 0; step < 120; step++)
            world.Step(Vector3.Zero, step % 2 == 0 ? Quaternion.Identity : -Quaternion.Identity);
        Assert.True(world.CubeIsSleeping);
        world.Step(new Vector3(0, 0.02f, 0), Quaternion.Identity);
        Assert.False(world.CubeIsSleeping);
        for (int step = 0; step < 480; step++) world.Step(new Vector3(0, 0.02f, 0), Quaternion.Identity);
        Assert.True(world.CubeIsSleeping);
        world.CopyPoses();
        Assert.Equal(0, world.Escaped);
        Assert.InRange(world.Poses[0].Position.Y, -PhysicsWorld.HalfExtent + PhysicsWorld.Radius + 0.01f,
            -PhysicsWorld.HalfExtent + PhysicsWorld.Radius + 0.03f);
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void EmptySceneCanBeSteppedAndRecreated()
    {
        for (int repeat = 0; repeat < 2; repeat++)
        {
            using var world = new PhysicsWorld(0);
            Assert.NotEqual(0, world.AdapterLuid);
            world.Step(Vector3.Zero, Quaternion.Identity);
            world.CopyPoses();
            Assert.Empty(world.Poses);
            Assert.Equal(0, world.Escaped);
        }
    }

    [Theory]
    [InlineData(644)]
    [InlineData(824)]
    [InlineData(1440)]
    public void DragTranslationMatchesScreenPixels(int height)
    {
        float width = height * 16f / 9;
        Vector2 pixels = new(180, -90);
        Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(new Vector3(13, 10, 19), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, width / height, 0.1f, 100);
        foreach (Vector3 position in new[] { Vector3.Zero, new Vector3(2, -1, 3) })
        {
            Vector3 movement = MainWindow.DragTranslation(pixels, height, position);
            Vector4 before = Vector4.Transform(new Vector4(position, 1), viewProjection);
            Vector4 after = Vector4.Transform(new Vector4(position + movement, 1), viewProjection);
            Vector2 screenMovement = new((after.X / after.W - before.X / before.W) * width / 2,
                -(after.Y / after.W - before.Y / before.W) * height / 2);
            Assert.InRange(Vector2.Distance(pixels, screenMovement), 0, 0.01f);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "GPU")]
    public void FastDragAndReversalRetainsBalls(int count)
    {
        using var world = new PhysicsWorld(count);
        var controller = new CubeController();
        for (int step = 0; step < 240; step++)
        {
            controller.TargetPosition = new Vector3(2 * MathF.Sin(step * 0.07f), 0.5f * MathF.Sin(step * 0.09f), 0);
            controller.TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f * MathF.Sin(step * 0.06f));
            controller.Advance();
            world.Step(controller.Position, controller.Orientation);
            world.CopyPoses();
            Assert.Equal(0, world.Escaped);
        }
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void SingleBallSettlesOnBottomWall()
    {
        using var world = new PhysicsWorld(1);
        for (int step = 0; step < 120; step++) world.Step(Vector3.Zero, Quaternion.Identity);
        world.CopyPoses();
        Assert.InRange(world.Poses[0].Position.Y, -PhysicsWorld.HalfExtent + PhysicsWorld.Radius - 0.01f, -PhysicsWorld.HalfExtent + PhysicsWorld.Radius + 0.01f);
        Assert.Equal(0, world.Escaped);
    }

    [Fact]
    [Trait("Category", "GPU")]
    public void MovingRotatingCubeTransfersMotionAndRetainsBalls()
    {
        using var world = new PhysicsWorld(1000);
        Vector3 initial = world.Poses[0].Position;
        var controller = new CubeController { TargetPosition = new Vector3(1, 0.3f, 0), TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f) };
        for (int step = 0; step < 180; step++)
        {
            controller.Advance();
            world.Step(controller.Position, controller.Orientation);
            world.CopyPoses();
            Assert.Equal(0, world.Escaped);
        }
        Assert.True(Vector3.Distance(initial, world.Poses[0].Position) > 0.1f);
        Assert.All(world.Poses, pose => Assert.True(float.IsFinite(pose.Position.LengthSquared())));
    }
}