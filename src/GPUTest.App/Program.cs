using System.Runtime.InteropServices;
using System.Diagnostics;
using GPUTest.App.Diagnostics;
using GPUTest.App.Rendering;
using GPUTest.App.Simulation;
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace GPUTest.App;

internal static partial class Program
{
    [LibraryImport("GPUTest.Physics", EntryPoint = "gp_probe")]
    private static partial int ProbePhysics();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--render-probe") || (args.Length > 0 && args[0] == "--benchmark"))
            {
                bool benchmark = args[0] == "--benchmark";
                int ballCount = benchmark && args.Length > 1 ? int.Parse(args[1]) : 10_000;
                int measuredFrames = benchmark && args.Length > 2 ? int.Parse(args[2]) : benchmark ? 300 : 30;
                if (measuredFrames is < 1 or > 36000) throw new ArgumentOutOfRangeException(nameof(measuredFrames));
                int warmup = benchmark ? 120 : 0;
                double[] timings = new double[measuredFrames];
                ApplicationConfiguration.Initialize();
                using var window = new Form { Text = "GPUTest | Dual-GPU rendering probe", ClientSize = benchmark ? new Size(2560, 1440) : new Size(1280, 720), TopMost = true, FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false };
                window.Show();
                using var renderer = new DualGpuRenderer(window.Handle, window.ClientSize.Width, window.ClientSize.Height);
                using var world = new PhysicsWorld(ballCount);
                for (int frame = 0; frame < measuredFrames + warmup; frame++)
                {
                    Application.DoEvents();
                    if (window.IsDisposed) throw new OperationCanceledException("Benchmark window closed.");
                    long start = Stopwatch.GetTimestamp();
                    Vector3 translation = Vector3.Zero;
                    Quaternion rotation = Quaternion.Identity;
                    for (int substep = 0; substep < 2; substep++)
                    {
                        if (benchmark)
                        {
                            float time = (frame * 2 + substep) * PhysicsWorld.Timestep;
                            translation = new Vector3(0.5f * MathF.Sin(time * 2), 0.25f * MathF.Sin(time * 3), 0);
                            rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f * MathF.Sin(time * 1.5f));
                        }
                        world.Step(translation, rotation);
                    }
                    world.CopyPoses();
                    if (world.Escaped != 0) throw new InvalidOperationException($"Containment failed: {world.Escaped} escaped balls.");
                    renderer.Render(world.Poses, translation, rotation);
                    if (frame >= warmup) timings[frame - warmup] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                if (benchmark)
                {
                    Array.Sort(timings);
                    double average = timings.Average();
                    double p95 = timings[(int)((measuredFrames - 1) * 0.95)];
                    bool targetMet = average <= 1000.0 / 60 && p95 <= 20;
                    Console.WriteLine($"COMBINED: {ballCount:N0} balls, {window.ClientSize.Width}x{window.ClientSize.Height}, 2 GPUs, {measuredFrames} measured frames, average={average:F2}ms, p95={p95:F2}ms, FPS={1000 / average:F2}, realtime-factor={1000 / 60 / average:F2}, escaped=0");
                    Console.WriteLine($"60 FPS performance gate: {(targetMet ? "MET for this short run" : "NOT MET")}. Visuals are the wireframe prototype, not the final glass renderer.");
                    return 0;
                }
                Directory.CreateDirectory("artifacts");
                using var screenshot = new Bitmap(window.ClientSize.Width, window.ClientSize.Height);
                using (Graphics graphics = Graphics.FromImage(screenshot))
                    graphics.CopyFromScreen(window.PointToScreen(Point.Empty), Point.Empty, screenshot.Size);
                screenshot.Save("artifacts/render-probe.png");
                for (int region = 0; region < 2; region++)
                {
                    int coloredPixels = 0;
                    for (int vertical = 8; vertical < screenshot.Height - 8; vertical += 4)
                        for (int horizontal = region * screenshot.Width / 2 + 8; horizontal < (region + 1) * screenshot.Width / 2 - 8; horizontal += 4)
                        {
                            Color pixel = screenshot.GetPixel(horizontal, vertical);
                            if (pixel.R > 50 || pixel.G > 50 || pixel.B > 50) coloredPixels++;
                        }
                    if (coloredPixels < 100) throw new InvalidOperationException($"GPU region {region} appears blank ({coloredPixels} scene pixels).");
                    Console.WriteLine($"GPU region {region}: {coloredPixels} sampled non-background pixels.");
                }
                Console.WriteLine($"PASS: {renderer.FrameId} frames composed from two GPU render targets.");
                return 0;
            }
            if (args.Length == 0 || args.Contains("--ui-smoke"))
            {
                ApplicationConfiguration.Initialize();
                using var window = new MainWindow(args.Contains("--ui-smoke"));
                Application.Run(window);
                if (window.Failure is not null) throw window.Failure;
                return 0;
            }
            if (args.Length > 0 && args[0] == "--physics-benchmark")
            {
                PhysicsBenchmark.Run(args.Length > 1 ? int.Parse(args[1]) : 10_000, args.Length > 2 ? int.Parse(args[2]) : 300);
                return 0;
            }
            if (args.Contains("--physics-probe"))
            {
                int result = ProbePhysics();
                if (result != 0) throw new InvalidOperationException($"PhysX CUDA initialization failed ({result}).");
                Console.WriteLine("PASS: native PhysX GPU runtime loaded from application output.");
                return 0;
            }
            if (args.Length != 1 || args[0] != "--probe")
                throw new ArgumentException("Use no arguments for the UI, or --probe, --physics-probe, --render-probe, --ui-smoke, --physics-benchmark [balls] [frames], --benchmark [balls] [frames].");
            using IDXGIFactory4 factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            int count = 0;
            for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).Success; index++)
            {
                using (adapter)
                {
                    AdapterDescription1 description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0 || description.VendorId != 0x10DE)
                        continue;
                    using ID3D12Device device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0);
                    Console.WriteLine($"GPU {count}: {description.Description.Trim()} | LUID {description.Luid} | {description.DedicatedVideoMemory / (1024 * 1024)} MiB | D3D12 ready");
                    count++;
                }
            }

            if (count < 2)
                throw new InvalidOperationException($"Two NVIDIA D3D12 adapters are required; found {count}.");
            Console.WriteLine("PASS: two independent NVIDIA Direct3D 12 devices created.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}