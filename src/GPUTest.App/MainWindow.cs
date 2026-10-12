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
            if (suspended) return;
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
            if (simulation.TrySwapSnapshot(ref snapshot)) slowMotionSinceStatus |= snapshot.SlowingDown;
            if (snapshot.Generation != generation) return;
            UseWaitCursor = false;
            if (renderer is null || resizePending || renderer.AdapterLuid != snapshot.AdapterLuid)
            {
                renderer?.Dispose();
                renderer = null;
                renderer = new GpuRenderer(viewport.Handle, viewport.Width, viewport.Height, snapshot.AdapterLuid);
                resizePending = false;
            }
            if (!renderer.TryRender(snapshot.Poses, snapshot.CubePosition, snapshot.CubeOrientation)) return;
            if (smokeTest) RunSmokeFrame();
            frames++;
            double statusElapsed = Stopwatch.GetElapsedTime(lastStatusTick).TotalSeconds;
            if (statusElapsed >= 0.5)
            {
                string state = paused ? "Paused" : slowMotionSinceStatus ? "Slow motion (physics overload)" : "Running";
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

    private void RunSmokeFrame()
    {
        smokeFrame++;
        if (smokeDragStart != 0 && smokeDragMilliseconds < 0
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
            renderTimer.Stop();
            BeginInvoke(Close);
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