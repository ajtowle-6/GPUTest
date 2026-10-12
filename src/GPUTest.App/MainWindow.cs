using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using GPUTest.App.Rendering;
using GPUTest.App.Simulation;

namespace GPUTest.App;

internal sealed partial class MainWindow : Form
{
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(14, 17, 18), TabStop = true };
    private readonly NumericUpDown count = new() { Minimum = 0, Maximum = 100_000, Increment = 1_000, Value = 10_000, ThousandsSeparator = true, Width = 110 };
    private readonly Button pause = new() { Text = "Pause", AutoSize = true };
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private PhysicsWorld? world;
    private DualGpuRenderer? renderer;
    private CubeController cube = new();
    private bool paused;
    private bool resizePending;
    private bool shuttingDown;
    private MouseButtons dragging;
    private Point previousMouse;
    private readonly SimulationClock clock = new();
    private bool slowMotionSinceStatus;
    private long lastTick;
    private long lastStatusTick;
    private int frames;
    private readonly bool smokeTest;
    private int smokeFrame;
    private bool smokeOverloadObserved;
    private bool smokeMessageProcessed;
    private int smokeStepsAfterOverload;
    public Exception? Failure { get; private set; }

    public MainWindow(bool smokeTest = false)
    {
        this.smokeTest = smokeTest;
        if (smokeTest) TopMost = true;
        Text = "GPUTest | GPU physics and dual-GPU rendering prototype";
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
            SetPaused(true);
            if (world is null) return;
            try { AdvancePhysics(); world.CopyPoses(); }
            catch (Exception exception) { Fail(exception); }
        };
        viewport.Resize += (_, _) => resizePending = true;
        viewport.MouseDown += (_, args) => { dragging = args.Button; previousMouse = args.Location; viewport.Capture = true; viewport.Focus(); };
        viewport.MouseUp += (_, _) => { dragging = MouseButtons.None; viewport.Capture = false; };
        viewport.MouseCaptureChanged += (_, _) => { if (!viewport.Capture) dragging = MouseButtons.None; };
        viewport.MouseMove += MoveCube;
        viewport.MouseWheel += (_, args) => cube.TargetPosition = Vector3.Clamp(cube.TargetPosition + Vector3.Normalize(new Vector3(13, 10, 19)) * (args.Delta / 600f), new Vector3(-4), new Vector3(4));
        Deactivate += (_, _) => { dragging = MouseButtons.None; viewport.Capture = false; };
        Shown += (_, _) =>
        {
            ResetSimulation();
            lastTick = lastStatusTick = Stopwatch.GetTimestamp();
            Application.Idle += OnIdle;
        };
        FormClosed += (_, _) =>
        {
            shuttingDown = true;
            Application.Idle -= OnIdle;
            renderer?.Dispose();
            world?.Dispose();
        };
    }

    private void ResetSimulation()
    {
        SetPaused(true);
        UseWaitCursor = true;
        status.Text = "Creating GPU simulation...";
        status.Owner?.Refresh();
        try
        {
            world?.Dispose();
            world = null;
            cube = new CubeController();
            world = new PhysicsWorld((int)count.Value);
            SetPaused(false);
            lastTick = Stopwatch.GetTimestamp();
        }
        catch (Exception exception) { Fail(exception); }
        finally { UseWaitCursor = false; }
    }

    private void SetPaused(bool value)
    {
        paused = value;
        pause.Text = paused ? "Resume" : "Pause";
        clock.Reset();
        slowMotionSinceStatus = false;
        lastTick = Stopwatch.GetTimestamp();
    }

    private void MoveCube(object? sender, MouseEventArgs args)
    {
        int horizontal = args.X - previousMouse.X;
        int vertical = args.Y - previousMouse.Y;
        previousMouse = args.Location;
        if (dragging == MouseButtons.Left)
        {
            cube.TargetPosition = Vector3.Clamp(cube.TargetPosition + DragTranslation(new Vector2(horizontal, vertical), viewport.Height, cube.Position), new Vector3(-4), new Vector3(4));
        }
        else if (dragging == MouseButtons.Right)
        {
            Quaternion change = Quaternion.CreateFromYawPitchRoll(horizontal * 0.005f, vertical * 0.005f, 0);
            cube.TargetOrientation = Quaternion.Normalize(change * cube.TargetOrientation);
        }
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

    private void AdvancePhysics()
    {
        cube.Advance();
        world!.Step(cube.Position, cube.Orientation);
        if (smokeTest && smokeOverloadObserved) smokeStepsAfterOverload++;
    }

    private void OnIdle(object? sender, EventArgs args)
    {
        while (!shuttingDown && !IsDisposed && !PeekMessage(out _, 0, 0, 0, 0))
        {
            if (WindowState == FormWindowState.Minimized || world is null || viewport.Width < 2 || viewport.Height < 1)
            {
                lastTick = Stopwatch.GetTimestamp();
                clock.Reset();
                return;
            }
            try
            {
                if (renderer is null || resizePending)
                {
                    renderer?.Dispose();
                    renderer = null;
                    renderer = new DualGpuRenderer(viewport.Handle, viewport.Width, viewport.Height);
                    resizePending = false;
                    lastTick = Stopwatch.GetTimestamp();
                    clock.Reset();
                }
                long now = Stopwatch.GetTimestamp();
                double elapsed = Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds;
                lastTick = now;
                if (!paused)
                {
                    if (smokeTest && smokeFrame == 20) elapsed += 0.75;
                    clock.BeginFrame(elapsed);
                    long physicsStart = Stopwatch.GetTimestamp();
                    while (!PeekMessage(out _, 0, 0, 0, 0)
                        && clock.TryTakeStep(Stopwatch.GetElapsedTime(physicsStart).TotalSeconds))
                    {
                        AdvancePhysics();
                    }
                    clock.EndFrame();
                    slowMotionSinceStatus |= clock.IsSlowingDown;
                    if (smokeTest && smokeFrame >= 20 && clock.IsSlowingDown) smokeOverloadObserved = true;
                    world.CopyPoses();
                    if (world.Escaped != 0) throw new InvalidOperationException($"Containment failure: {world.Escaped} escaped balls.");
                }
                renderer.Render(world.Poses, cube.Position, cube.Orientation);
                if (smokeTest)
                {
                    smokeFrame++;
                    if (paused && smokeFrame < 375) throw new InvalidOperationException("UI smoke test unexpectedly paused.");
                    if (smokeFrame == 10)
                    {
                        cube.TargetPosition = new Vector3(1, 0.4f, 0);
                        cube.TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f);
                    }
                    if (smokeFrame == 35) ClientSize = new Size(1024, 720);
                    if (smokeFrame == 21) BeginInvoke(() => smokeMessageProcessed = true);
                    if (smokeFrame is >= 30 and < 330)
                    {
                        float phase = (smokeFrame - 30) * 0.035f;
                        cube.TargetPosition = new Vector3(2 * MathF.Sin(phase), 0.6f * MathF.Sin(phase * 1.3f), 0);
                        cube.TargetOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f * MathF.Sin(phase * 0.75f));
                    }
                    if (smokeFrame == 355) { count.Value = 1000; ResetSimulation(); }
                    if (smokeFrame == 375) { SetPaused(true); AdvancePhysics(); world.CopyPoses(); }
                    if (smokeFrame == 385)
                    {
                        if (!smokeOverloadObserved || !smokeMessageProcessed || smokeStepsAfterOverload < 100)
                            throw new InvalidOperationException("UI smoke test did not demonstrate continued physics and UI message processing after overload.");
                        Directory.CreateDirectory("artifacts");
                        using var screenshot = new Bitmap(ClientSize.Width, ClientSize.Height);
                        using (Graphics graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(PointToScreen(Point.Empty), Point.Empty, screenshot.Size);
                        screenshot.Save("artifacts/ui-smoke.png");
                        Console.WriteLine($"PASS: sustained movement, overload recovery ({smokeStepsAfterOverload} subsequent physics steps), queued UI callback, resize, count reset, pause and single-step.");
                        BeginInvoke(Close);
                        return;
                    }
                }
                frames++;
                double statusElapsed = Stopwatch.GetElapsedTime(lastStatusTick).TotalSeconds;
                if (statusElapsed >= 0.5)
                {
                    string state = paused ? "Paused" : slowMotionSinceStatus ? "Slow motion (physics overload)" : "Running";
                    status.Text = $"{state}   |   {world.Poses.Length:N0} balls   |   {frames / statusElapsed:F1} render FPS   |   2 x RTX 4090 rendering";
                    frames = 0;
                    slowMotionSinceStatus = false;
                    lastStatusTick = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception exception) { Fail(exception); return; }
        }
    }

    private void Fail(Exception exception)
    {
        Failure = exception;
        SetPaused(true);
        Application.Idle -= OnIdle;
        status.Text = exception.Message;
        Console.Error.WriteLine(exception);
        if (smokeTest) { BeginInvoke(Close); return; }
        MessageBox.Show(this, exception.Message, "GPUTest stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out WindowMessage message, nint window, uint minimum, uint maximum, uint remove);
}