using System.Runtime.InteropServices;
using System.Diagnostics;
using GPUTest.App.Diagnostics;
using GPUTest.App.Rendering;
using GPUTest.App.Simulation;
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
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
            if (args.Contains("--gpu-validation"))
            {
                using ID3D12Debug debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
                debug.EnableDebugLayer();
                Console.WriteLine("D3D12 debug-layer validation enabled.");
            }
            if (args.Contains("--render-probe") || (args.Length > 0 && args[0] == "--benchmark"))
            {
                bool benchmark = args[0] == "--benchmark";
                int ballCount = benchmark && args.Length > 1 ? int.Parse(args[1]) : 10_000;
                int measuredFrames = benchmark && args.Length > 2 ? int.Parse(args[2]) : benchmark ? 300 : 30;
                if (measuredFrames is < 1 or > 36000) throw new ArgumentOutOfRangeException(nameof(measuredFrames));
                int warmup = benchmark ? 120 : 0;
                double[] timings = new double[measuredFrames];
                double physicsMilliseconds = 0;
                double renderingMilliseconds = 0;
                ApplicationConfiguration.Initialize();
                using var window = new Form { Text = "GPUTest | Single-GPU rendering probe", ClientSize = benchmark ? new Size(2560, 1440) : new Size(1280, 720), TopMost = true, FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false };
                window.Show();
                using var world = new PhysicsWorld(ballCount);
                using var renderer = new GpuRenderer(window.Handle, window.ClientSize.Width, window.ClientSize.Height, world.AdapterLuid);
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
                    long renderStart = Stopwatch.GetTimestamp();
                    renderer.Render(world.Poses, translation, rotation);
                    if (frame >= warmup)
                    {
                        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        double physicsElapsed = Stopwatch.GetElapsedTime(start, renderStart).TotalMilliseconds;
                        timings[frame - warmup] = elapsed;
                        physicsMilliseconds += physicsElapsed;
                        renderingMilliseconds += elapsed - physicsElapsed;
                    }
                }
                if (benchmark)
                {
                    long drainStart = Stopwatch.GetTimestamp();
                    renderer.WaitIdle();
                    double drainMilliseconds = Stopwatch.GetElapsedTime(drainStart).TotalMilliseconds;
                    timings[^1] += drainMilliseconds;
                    renderingMilliseconds += drainMilliseconds;
                    Array.Sort(timings);
                    double average = timings.Average();
                    double p95 = timings[(int)((measuredFrames - 1) * 0.95)];
                    bool targetMet = average <= 1000.0 / 60 && p95 <= 20;
                    Console.WriteLine($"COMBINED: {ballCount:N0} balls, {window.ClientSize.Width}x{window.ClientSize.Height}, 1 GPU [{renderer.AdapterLuid}], {measuredFrames} measured frames, average={average:F2}ms, p95={p95:F2}ms, FPS={1000 / average:F2}, realtime-factor={1000 / 60 / average:F2}, escaped=0");
                    Console.WriteLine($"BREAKDOWN: physics + poses={physicsMilliseconds / measuredFrames:F2}ms, render submission + waits/presentation={renderingMilliseconds / measuredFrames:F2}ms per frame (not GPU kernel timings)");
                    Console.WriteLine($"60 FPS performance gate: {(targetMet ? "MET for this short run" : "NOT MET")}. Visuals are the wireframe prototype, not the final glass renderer.");
                    return 0;
                }
                Directory.CreateDirectory("artifacts");
                using var screenshot = renderer.Capture();
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
                    if (coloredPixels < 100) throw new InvalidOperationException($"Image half {region} appears blank ({coloredPixels} scene pixels).");
                    Console.WriteLine($"Image half {region}: {coloredPixels} sampled non-background pixels.");
                }
                Console.WriteLine($"PASS: {renderer.FrameId} full-width frames; physics and rendering on one GPU [{renderer.AdapterLuid}].");
                return 0;
            }
            if (args.Length == 0 || args.Contains("--ui-smoke") || args[0] == "--balls")
            {
                if (args.Length == 1 && args[0] == "--balls") throw new ArgumentException("--balls requires a count from 0 to 100000.");
                int initialCount = args.Length > 1 && args[0] is "--balls" or "--ui-smoke" ? int.Parse(args[1]) : 10_000;
                ApplicationConfiguration.Initialize();
                using var window = new MainWindow(args.Contains("--ui-smoke"), initialCount);
                Application.Run(window);
                if (window.Failure is not null) throw window.Failure;
                return 0;
            }
            if (args.Length > 0 && args[0] == "--physics-benchmark")
            {
                int workersOption = Array.IndexOf(args, "--workers");
                if (workersOption == args.Length - 1) throw new ArgumentException("--workers requires a count from 0 to 64.");
                int workers = workersOption < 0 ? 4 : int.Parse(args[workersOption + 1]);
                PhysicsBenchmark.Run(args.Length > 1 ? int.Parse(args[1]) : 10_000, args.Length > 2 ? int.Parse(args[2]) : 300,
                    args.Contains("--profile"), workers, args.Contains("--queries"));
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
                throw new ArgumentException("Use no arguments or --balls <count> for the UI, or --probe, --physics-probe, --render-probe, --ui-smoke [balls], --physics-benchmark [balls] [frames] [--workers 0..64] [--queries] [--profile], --benchmark [balls] [frames].");
            using IDXGIFactory4 factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            using var probeWorld = new PhysicsWorld(0);
            for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).Success; index++)
            {
                using (adapter)
                {
                    AdapterDescription1 description = adapter.Description1;
                    if (description.Luid != probeWorld.AdapterLuid || (description.Flags & AdapterFlags.Software) != 0 || description.VendorId != 0x10DE)
                        continue;
                    using ID3D12Device device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0);
                    Console.WriteLine($"GPU: {description.Description.Trim()} | LUID {description.Luid} | {description.DedicatedVideoMemory / (1024 * 1024)} MiB | D3D12 ready");
                    Console.WriteLine("PASS: one NVIDIA GPU selected for both PhysX and Direct3D 12.");
                    return 0;
                }
            }

            throw new InvalidOperationException($"The PhysX adapter [{probeWorld.AdapterLuid}] is not available for D3D12 rendering.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}