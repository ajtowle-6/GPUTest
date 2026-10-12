using System.Diagnostics;
using System.Numerics;
using GPUTest.App.Simulation;

namespace GPUTest.App.Diagnostics;

internal static class PhysicsBenchmark
{
    public static void Run(int count, int frames, bool profile = false, int workers = 4, bool sceneQueries = false)
    {
        if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
        using var world = new PhysicsWorld(count, workers, sceneQueries);
        double[] milliseconds = new double[frames];
        double[] stepMilliseconds = new double[frames];
        double[] copyMilliseconds = new double[frames];
        for (int frame = 0; frame < frames + 120; frame++)
        {
            if (profile && frame == 120) world.BeginProfile();
            long start = Stopwatch.GetTimestamp();
            for (int substep = 0; substep < 2; substep++)
            {
                float time = (frame * 2 + substep) * PhysicsWorld.Timestep;
                Vector3 translation = new(0.5f * MathF.Sin(time * 2), 0.25f * MathF.Sin(time * 3), 0);
                Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f * MathF.Sin(time * 1.5f));
                world.Step(translation, rotation);
            }
            long copyStart = Stopwatch.GetTimestamp();
            world.CopyPoses();
            if (world.Escaped != 0) throw new InvalidOperationException($"Containment failed: {world.Escaped} balls escaped at frame {frame}.");
            if (frame >= 120)
            {
                int measured = frame - 120;
                milliseconds[measured] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                stepMilliseconds[measured] = Stopwatch.GetElapsedTime(start, copyStart).TotalMilliseconds;
                copyMilliseconds[measured] = milliseconds[measured] - stepMilliseconds[measured];
            }
        }
        Array.Sort(milliseconds);
        double average = milliseconds.Average();
        double percentile95 = milliseconds[(int)((frames - 1) * 0.95)];
        Console.WriteLine($"PHYSICS ONLY: balls={count:N0}, frames={frames}, average={average:F2}ms, p95={percentile95:F2}ms, realtime-factor={1000 / 60 / average:F2}, escaped=0");
        Console.WriteLine($"BREAKDOWN: two physics steps={stepMilliseconds.Average():F2}ms, pose copy + validation={copyMilliseconds.Average():F2}ms per frame");
        Console.WriteLine("Rendering and presentation are NOT included. This is not the application performance acceptance test.");
        if (profile) world.ReportProfile(frames);
    }
}