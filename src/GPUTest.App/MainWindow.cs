using System.Diagnostics;
using System.Numerics;
using GPUTest.App.Rendering;
using GPUTest.App.Simulation;

namespace GPUTest.App;

internal sealed partial class MainWindow : Form
{
    internal const int DefaultBallCount = 1_000;

    private readonly Panel viewport = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(14, 17, 18), TabStop = true };
    private readonly NumericUpDown count = new() { Minimum = 0, Maximum = 100_000, Increment = 1_000, Value = DefaultBallCount, ThousandsSeparator = true, Width = 110 };
    private readonly Button pause = new() { Text = "Pause", AutoSize = true };
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private PhysicsWorker? simulation;
    private PhysicsSnapshot snapshot = new();
    private GpuRenderer? renderer;
    private Vector3 targetPosition;
    private Quaternion targetOrientation = Quaternion.Identity;
    private int generation = 1;
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 1 };
    private bool paused;
    private bool resizePending;
    private bool framePending = true;
    private bool shuttingDown;
    private bool shutdownComplete;
    private MouseButtons dragging;
    private Point previousMouse;
    private bool slowMotionSinceStatus;
    private long lastStatusTick;
    private long lastStatusSteps;
    private int frames;
    private readonly bool smokeTest;
    private readonly int initialBallCount;
    private long smokeDragStart;
    private double smokeDragMilliseconds = -1;
    private int smokeFrame;
    private bool smokeMessageProcessed;
    private readonly ManualResetEventSlim smokeStepBlocked = new();
    private readonly ManualResetEventSlim smokeReleaseStep = new();
    private int smokeBlockRequested;
    private long smokeBlockStart;
    private long smokeStartSteps;
    private bool smokeCallbackQueued;
    private double smokeCallbackMilliseconds;
    private long smokeManualStep = -1;
    private long smokePhysicsCalls;
    public Exception? Failure { get; private set; }

    public MainWindow(bool smokeTest = false, int initialBallCount = DefaultBallCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialBallCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialBallCount, 100_000);
        this.smokeTest = smokeTest;
        this.initialBallCount = initialBallCount;
        count.Value = initialBallCount;
        if (smokeTest) TopMost = true;
        Text = "GPUTest | Single-GPU physics and rendering prototype";
        ClientSize = new Size(1440, 900);
        MinimumSize = new Size(800, 600);
        Font = new Font("Bahnschrift", 10);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 5, 8, 5), BackColor = Color.FromArgb(241, 243, 244), WrapContents = false };
        var apply = new Button { Text = "Apply / Reset", AutoSize = true };
        var step = new Button { Text = "Step", AutoSize = true };
        toolbar.Controls.AddRange([new Label { Text = "Balls", AutoSize = true, Padding = new Padding(0, 5, 4, 0) }, count, apply, pause, step]);
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(status);
        Controls.Add(viewport);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);
        apply.Click += (_, _) => ResetSimulation();
        pause.Click += (_, _) => SetPaused(!paused);
        step.Click += (_, _) =>
        {
            if (!paused) SetPaused(true);
            simulation?.RequestStep();
        };
        viewport.Resize += (_, _) => resizePending = true;
        viewport.Paint += (_, _) => framePending = true;
        viewport.MouseDown += (_, args) => { dragging = args.Button; previousMouse = args.Location; viewport.Capture = true; viewport.Focus(); };
        viewport.MouseUp += (_, _) => { dragging = MouseButtons.None; viewport.Capture = false; };
        viewport.MouseCaptureChanged += (_, _) => { if (!viewport.Capture) dragging = MouseButtons.None; };
        viewport.MouseMove += MoveCube;
        viewport.MouseWheel += (_, args) =>
        {
            targetPosition = Vector3.Clamp(targetPosition + Vector3.Normalize(new Vector3(13, 10, 19)) * (args.Delta / 600f), new Vector3(-4), new Vector3(4));
            PublishTarget();
        };
        Deactivate += (_, _) => { dragging = MouseButtons.None; viewport.Capture = false; };
        Shown += (_, _) =>
        {
            ResetSimulation();
            lastStatusTick = Stopwatch.GetTimestamp();
            renderTimer.Start();
        };
        renderTimer.Tick += OnFrame;
        FormClosing += async (_, args) =>
        {
            if (shutdownComplete) return;
            args.Cancel = true;
            if (shuttingDown) return;
            shuttingDown = true;
            renderTimer.Stop();
            smokeReleaseStep.Set();
            try
            {
                if (simulation is not null) await simulation.StopAsync();
                renderer?.Dispose();
            }
            catch (Exception exception) { Failure ??= exception; Console.Error.WriteLine(exception); }
            finally
            {
                renderTimer.Dispose();
                smokeStepBlocked.Dispose();
                smokeReleaseStep.Dispose();
                shutdownComplete = true;
                BeginInvoke(Close);
            }
        };
    }

    private void ResetSimulation()
    {
        UseWaitCursor = true;
        status.Text = "Creating GPU simulation...";
        targetPosition = Vector3.Zero;
        targetOrientation = Quaternion.Identity;
        if (simulation is null)
        {
            simulation = new PhysicsWorker((int)count.Value, beforeStep: smokeTest ? BeforeSmokeStep : null);
        }
        else generation = simulation.Reset((int)count.Value);
        SetPaused(false);
    }

    private void SetPaused(bool value)
    {
        paused = value;
        pause.Text = paused ? "Resume" : "Pause";
        simulation?.SetPaused(value);
        slowMotionSinceStatus = false;
    }

    private void MoveCube(object? sender, MouseEventArgs args)
    {
        int horizontal = args.X - previousMouse.X;
        int vertical = args.Y - previousMouse.Y;
        previousMouse = args.Location;
        if (dragging == MouseButtons.Left)
        {
            targetPosition = Vector3.Clamp(targetPosition + DragTranslation(new Vector2(horizontal, vertical), viewport.Height, snapshot.CubePosition), new Vector3(-4), new Vector3(4));
        }
        else if (dragging == MouseButtons.Right)
        {
            Quaternion change = Quaternion.CreateFromYawPitchRoll(horizontal * 0.005f, vertical * 0.005f, 0);
            targetOrientation = Quaternion.Normalize(change * targetOrientation);
        }
        if (dragging is MouseButtons.Left or MouseButtons.Right) PublishTarget();
    }

    internal static Vector3 DragTranslation(Vector2 pixels, int viewportHeight, Vector3 position)
    {
        Vector3 eye = new(13, 10, 19);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        Vector3 right = new(view.M11, view.M21, view.M31);
        Vector3 up = new(view.M12, view.M22, view.M32);
        float depth = Vector3.Dot(position - eye, Vector3.Normalize(-eye));
        float worldUnitsPerPixel = 2 * depth * MathF.Tan(MathF.PI / 8) / Math.Max(1, viewportHeight);
        return (right * pixels.X - up * pixels.Y) * worldUnitsPerPixel;
    }

    private void PublishTarget() => simulation?.SetTarget(targetPosition, targetOrientation);

    private void BeforeSmokeStep()
    {
        Interlocked.Increment(ref smokePhysicsCalls);
        if (Interlocked.Exchange(ref smokeBlockRequested, 0) == 0) return;
        Volatile.Write(ref smokeBlockStart, Stopwatch.GetTimestamp());
        smokeStepBlocked.Set();
        if (!smokeReleaseStep.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("UI did not release the blocked physics step.");
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (shuttingDown || IsDisposed || simulation is null) return;
        try
        {
            if (simulation.Failure is { } failure) throw failure;
            bool suspended = WindowState == FormWindowState.Minimized || viewport.Width < 2 || viewport.Height < 1;
            simulation.SetSuspended(suspended);
            if (suspended) { framePending = true; return; }
            if (smokeTest && smokeStepBlocked.IsSet && !smokeCallbackQueued)
            {
                smokeCallbackQueued = true;
                BeginInvoke(() =>
                {
                    smokeCallbackMilliseconds = Stopwatch.GetElapsedTime(Volatile.Read(ref smokeBlockStart)).TotalMilliseconds;
                    smokeMessageProcessed = true;
                    smokeReleaseStep.Set();
                });
            }
            long previousSteps = snapshot.Steps;
            int previousGeneration = snapshot.Generation;
            if (simulation.TrySwapSnapshot(ref snapshot))
            {
                framePending |= snapshot.Steps != previousSteps || snapshot.Generation != previousGeneration;
                slowMotionSinceStatus |= snapshot.SlowingDown;
            }
            if (snapshot.Generation != generation) return;
            UseWaitCursor = false;
            if (renderer is null || resizePending || renderer.AdapterLuid != snapshot.AdapterLuid)
            {
                renderer?.Dispose();
                renderer = null;
                renderer = new GpuRenderer(viewport.Handle, viewport.Width, viewport.Height, snapshot.AdapterLuid);
                resizePending = false;
                framePending = true;
            }
            bool rendered = framePending && renderer.TryRender(snapshot.Poses, snapshot.CubePosition, snapshot.CubeOrientation);
            if (rendered)
            {
                framePending = false;
                frames++;
            }
            if (smokeTest) RunSmokeFrame(rendered);
            double statusElapsed = Stopwatch.GetElapsedTime(lastStatusTick).TotalSeconds;
            if (statusElapsed >= 0.5)
            {
                string state = paused ? "Paused" : snapshot.IsSleeping ? "Idle" : slowMotionSinceStatus ? "Slow motion (physics overload)" : "Running";
                double simulationRate = (snapshot.Steps - lastStatusSteps) * PhysicsWorld.Timestep / statusElapsed;
                status.Text = $"{state}   |   {snapshot.Poses.Length:N0} balls   |   {frames / statusElapsed:F1} render FPS   |   sim {simulationRate:F2}x   |   1 GPU: {renderer.DeviceName}";
                frames = 0;
                lastStatusSteps = snapshot.Steps;
                slowMotionSinceStatus = false;
                lastStatusTick = Stopwatch.GetTimestamp();
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void RunSmokeFrame(bool rendered)
    {
        if (smokeFrame >= 385) return;
        smokeFrame++;
        if (rendered && smokeDragStart != 0 && smokeDragMilliseconds < 0
            && Vector3.DistanceSquared(snapshot.CubePosition, targetPosition) < 0.000001f
            && MathF.Abs(Quaternion.Dot(snapshot.CubeOrientation, targetOrientation)) > 0.99999f)
            smokeDragMilliseconds = Stopwatch.GetElapsedTime(smokeDragStart).TotalMilliseconds;
        if (paused && smokeFrame < 375) throw new InvalidOperationException("UI smoke test unexpectedly paused.");
        if (smokeFrame == 10)
        {
            smokeDragStart = Stopwatch.GetTimestamp();
            dragging = MouseButtons.Left;
            previousMouse = new Point(100, 100);
            MoveCube(viewport, new MouseEventArgs(MouseButtons.Left, 0, 150, 80, 0));
            dragging = MouseButtons.Right;
            MoveCube(viewport, new MouseEventArgs(MouseButtons.Right, 0, 180, 90, 0));
            dragging = MouseButtons.None;
        }
        if (smokeFrame == 20)
        {
            if (initialBallCount <= 1000
                && (smokeDragMilliseconds < 0 || smokeDragMilliseconds > 100))
                throw new InvalidOperationException($"Direct mouse tracking failed: input-to-render submission={smokeDragMilliseconds:F1}ms.");
            smokeStartSteps = snapshot.Steps;
            Interlocked.Exchange(ref smokeBlockRequested, 1);
        }
        if (smokeFrame == 35) ClientSize = new Size(1024, 720);
        if (smokeFrame is >= 30 and < 330)
        {
            float phase = (smokeFrame - 30) * 0.035f;
            targetPosition = new Vector3(2 * MathF.Sin(phase), 0.6f * MathF.Sin(phase * 1.3f), 0);
            targetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f * MathF.Sin(phase * 0.75f));
            PublishTarget();
        }
        if (smokeFrame == 355) { count.Value = 1000; ResetSimulation(); }
        if (smokeFrame == 375) SetPaused(true);
        if (smokeFrame > 375 && snapshot.Paused && smokeManualStep < 0)
        {
            smokeManualStep = snapshot.Steps;
            simulation!.RequestStep();
        }
        if (smokeFrame == 385)
        {
            long subsequentSteps = snapshot.Steps - smokeStartSteps;
            if (!smokeMessageProcessed || smokeCallbackMilliseconds > 250 || subsequentSteps < 100)
                throw new InvalidOperationException($"UI responsiveness check failed: callback={smokeCallbackMilliseconds:F1}ms, steps={subsequentSteps}.");
            if (smokeManualStep < 0 || !snapshot.Paused || snapshot.Steps != smokeManualStep + 1)
                throw new InvalidOperationException("Paused single-step did not advance exactly once.");
            Directory.CreateDirectory("artifacts");
            using var screenshot = renderer!.Capture();
            screenshot.Save("artifacts/ui-smoke.png");
            Console.WriteLine($"PASS: UI callback during blocked physics step ({smokeCallbackMilliseconds:F1}ms), {subsequentSteps} subsequent steps, sustained movement, resize, count reset, pause and exact single-step.");
            if (initialBallCount <= 1000)
                Console.WriteLine($"PASS: {initialBallCount} balls, mouse-handler input to matching render submission={smokeDragMilliseconds:F1}ms (not display scanout latency).");
            _ = RunIdleSmokeAsync();
        }
    }

    private async Task RunIdleSmokeAsync()
    {
        async Task WaitForAsync(Func<bool> condition, string operation)
        {
            long start = Stopwatch.GetTimestamp();
            while (!condition())
            {
                if (shuttingDown) throw new OperationCanceledException("Idle check window closed.");
                if (Stopwatch.GetElapsedTime(start).TotalSeconds > 30)
                    throw new TimeoutException($"Idle check timed out: {operation}.");
                await Task.Delay(10);
            }
        }

        async Task CheckQuietAsync()
        {
            long calls = Volatile.Read(ref smokePhysicsCalls);
            var submissions = renderer!.FrameId;
            await Task.Delay(350);
            if (Volatile.Read(ref smokePhysicsCalls) != calls || renderer.FrameId != submissions)
                throw new InvalidOperationException("Sleeping/paused scene continued submitting physics or rendering work.");
        }

        try
        {
            SetPaused(false);
            await WaitForAsync(() => !snapshot.Paused && snapshot.IsSleeping && !framePending, "natural settling");
            targetOrientation = -targetOrientation;
            PublishTarget();
            await CheckQuietAsync();

            long callsBeforeResize = Volatile.Read(ref smokePhysicsCalls);
            var previousRenderer = renderer;
            ClientSize = new Size(1100, 760);
            await WaitForAsync(() => renderer != previousRenderer && renderer!.FrameId > 0 && !framePending, "idle resize");
            var framesBeforePaint = renderer!.FrameId;
            viewport.Invalidate();
            await WaitForAsync(() => renderer.FrameId > framesBeforePaint && !framePending, "idle repaint");
            if (Volatile.Read(ref smokePhysicsCalls) != callsBeforeResize)
                throw new InvalidOperationException("Resize or repaint woke sleeping physics.");
            await CheckQuietAsync();

            SetPaused(true);
            await WaitForAsync(() => snapshot.Paused && !framePending, "idle pause");
            long stepsBeforeManual = snapshot.Steps;
            long callsBeforeManual = Volatile.Read(ref smokePhysicsCalls);
            var framesBeforeManual = renderer.FrameId;
            simulation!.RequestStep();
            await WaitForAsync(() => snapshot.Steps == stepsBeforeManual + 1 && !framePending
                && renderer.FrameId > framesBeforeManual, "idle single-step");
            if (Volatile.Read(ref smokePhysicsCalls) != callsBeforeManual + 1)
                throw new InvalidOperationException("Sleeping scene did not execute exactly one requested step.");
            await CheckQuietAsync();
            SetPaused(false);
            await WaitForAsync(() => !snapshot.Paused && snapshot.IsSleeping && !framePending, "idle resume");

            var framesBeforeMovement = renderer.FrameId;
            long stepsBeforeMovement = snapshot.Steps;
            dragging = MouseButtons.Left;
            previousMouse = new Point(100, 100);
            MoveCube(viewport, new MouseEventArgs(MouseButtons.Left, 0, 104, 98, 0));
            dragging = MouseButtons.Right;
            MoveCube(viewport, new MouseEventArgs(MouseButtons.Right, 0, 106, 99, 0));
            dragging = MouseButtons.None;
            await WaitForAsync(() => snapshot.Steps > stepsBeforeMovement && !framePending
                && renderer.FrameId > framesBeforeMovement && snapshot.CubePosition == targetPosition
                && MathF.Abs(Quaternion.Dot(snapshot.CubeOrientation, targetOrientation)) > 0.99999f, "mouse wake");
            long stepsAfterMovement = snapshot.Steps;
            await WaitForAsync(() => snapshot.Steps >= stepsAfterMovement + 4, "balls moving after mouse release");
            SetPaused(true);
            await WaitForAsync(() => snapshot.Paused && !framePending, "pause moving balls");
            await CheckQuietAsync();

            ResetSimulation();
            await WaitForAsync(() => snapshot.Generation == generation && !snapshot.Paused
                && snapshot.Steps > stepsBeforeManual + 1 && snapshot.IsSleeping && !framePending, "idle reset");
            await CheckQuietAsync();
            using var screenshot = renderer.Capture();
            screenshot.Save("artifacts/idle-smoke.png");
            Console.WriteLine("PASS: naturally sleeping scene submits zero physics steps and zero render frames across 350ms quiet intervals; equivalent target stays idle; resize/repaint redraw without physics; mouse, exact single-step and reset wake correctly.");
            renderTimer.Stop();
            BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            if (!shuttingDown) Fail(exception);
        }
    }

    private void Fail(Exception exception)
    {
        Failure = exception;
        SetPaused(true);
        renderTimer.Stop();
        UseWaitCursor = false;
        status.Text = exception.Message;
        Console.Error.WriteLine(exception);
        if (smokeTest) { BeginInvoke(Close); return; }
        MessageBox.Show(this, exception.Message, "GPUTest stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

}