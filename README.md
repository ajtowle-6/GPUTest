# GPUTest

C# / .NET 10 Windows desktop **feasibility prototype** for GPU rigid-body physics and explicit rendering on two NVIDIA GPUs.

## Status

Implemented: PhysX GPU sphere collisions, a mouse-translated/rotated six-wall kinematic container, 0-100,000 balls, pause/step/reset, and same-frame D3D12 rendering on two distinct adapters. Both GPUs render screen regions; the secondary region travels through a shared buffer and fence into the primary swap chain. No rendered image is read back to the CPU for composition.

**Not finished:** the cube is currently wireframe, not optically shaded glass. The 100,000-ball real-time performance requirement is **not met**. This milestone intentionally stops short of visual polish because the physics feasibility gate failed. No lower ball count or CPU renderer is silently substituted.

## Run

On this workstation, dependencies have already been restored and the native bridge built:

```powershell
dotnet run --project src/GPUTest.App -c Release
```

In VS Code, select **GPUTest prototype** and press F5. The build task restores dependencies, builds the native bridge, and builds the .NET solution. The C# extension is required for debugging; extension recommendations are included.

First build on another machine:

```powershell
./scripts/build.ps1
```

Prerequisites: Windows x64, .NET SDK 10.0.401 (or a later patch in that feature band), two NVIDIA D3D12 adapters, a compatible NVIDIA driver, Git with Git LFS, and Visual Studio 2022/2026 C++ Build Tools including CMake and a Windows SDK. A CUDA Toolkit installation is not needed for this pinned prebuilt GPU runtime.

The build downloads PhysX source and its declared dependencies. NVIDIA Packman maintains its own package cache and may initialize the user-level `PM_PACKAGES_ROOT` environment variable. On the development machine that cache is `D:\packman-repo`; no driver or CUDA Toolkit installation was performed.

For incremental builds after dependencies are restored:

```powershell
./scripts/build.ps1 -SkipRestore
```

## Controls

| Control | Action |
| --- | --- |
| Left drag in viewport | Translate cube in camera plane |
| Right drag | Rotate cube |
| Mouse wheel | Move cube toward/away from camera |
| Balls + Apply / Reset | Recreate a non-overlapping population; resets cube pose |
| Pause / Resume | Pause or resume simulation |
| Step | Pause and advance one 1/120-second step |

Mouse translation uses the camera's projected scale at the cube's current depth, so cursor and target displacement match on screen even after resizing. Motion is limited to 10 m/s translation, 80 m/s^2 translational acceleration and 1 rad/s rotation. Cube center is limited to +/-4 m along each world axis. These are prototype limits, not a guarantee for arbitrary mouse motion. Fast input can still reach these limits, and physics overload can still slow simulation. The camera is fixed for now.

The cube is 10 m wide internally and all balls have radius 0.08 m. Counts do not silently change sphere size. Balls start in an ordered, non-overlapping bottom-up lattice. Physics catch-up has an 8 ms work budget and yields when UI messages are pending. At least one due step is allowed when there are no pending messages; an individual synchronous physics step can exceed that budget. When work cannot keep up, the app drops excess wall-clock catch-up time, keeps the 1/120-second physics timestep, and displays **Slow motion (physics overload)** instead of permanently auto-pausing. Ball count, solver settings and collision detection are unchanged. This is an explicit slow-motion fallback, not achievement of the real-time performance target. Severe runtime errors still stop the loop and should be followed by restarting the app; this scheduling change cannot recover a hung GPU driver or preempt an in-progress native call.

## Validation

```powershell
dotnet test GPUTest.slnx -c Release
dotnet run --project src/GPUTest.App -c Release -- --probe
dotnet run --project src/GPUTest.App -c Release -- --physics-probe
dotnet run --project src/GPUTest.App -c Release -- --render-probe
dotnet run --project src/GPUTest.App -c Release -- --ui-smoke
dotnet run --project src/GPUTest.App -c Release -- --physics-benchmark 100000 120
dotnet run --project src/GPUTest.App -c Release -- --benchmark 100000 120
```

Fifteen test cases cover native pose layout, invalid counts, bounded control motion, fast drag/reversal response, screen-space drag mapping at multiple viewport sizes and depths, overload scheduling/recovery, GPU scene destruction/recreation, settling and moving/rotating containment. The rapid-motion containment test uses 10,000 balls. GPU tests require the native runtime and an NVIDIA GPU and are not silently skipped.

The finite rendering probe opens a window, renders 30 frames, and checks non-background pixels in both halves of a screen capture. Keep the probe unobscured on an unlocked desktop. It writes `artifacts/render-probe.png`. The 385-frame UI smoke test exercises sustained programmatic cube motion with 10,000 balls, injects a 750 ms wall-clock delay, and requires continued physics and queued UI callback processing after overload. It also covers resizing, count reset, manual pause and stepping, and writes `artifacts/ui-smoke.png`. It is not a substitute for exhaustive mouse-input testing or a long soak test.

Benchmark arguments are ball count and measured frame count. Benchmarks warm up for 120 frames and use a fixed translation/rotation trace with two 1/120-second physics steps per rendered frame. The combined benchmark renders at 2560x1440. A completed measurement exits successfully even when the printed performance gate is NOT MET. An initialization, containment, native warning, or rendering failure exits unsuccessfully. These short runs are feasibility measurements, not the planned long-duration acceptance suite.

## Measured Results

Windows 10.0.26200, .NET SDK 10.0.401, NVIDIA driver 617.42, two RTX 4090s. Adapter LUIDs during this session were 113200 and 119357; these IDs are not persistent device identifiers.

| Workload | Average | p95 | Result |
| --- | ---: | ---: | --- |
| 10,000 balls, physics + pose copy | 9.23 ms | 10.72 ms | No escaped balls |
| 100,000 balls, physics + pose copy | 62.17 ms | 67.27 ms | No escaped balls; too slow |
| 100,000 balls, physics + two-GPU rendering at 1440p | 63.79 ms | 68.13 ms | 15.68 FPS; 60 FPS gate NOT MET |

Physics warnings are treated as errors. The initial 100k run identified insufficient `foundLostPairsCapacity`; the corrected bridge allocates four million pair entries and completes the recorded trace without the overflow. More extreme motion may require further capacity/containment work.

## Implementation

- `src/GPUTest.App`: WinForms host, bounded cube controller, native interop, D3D12 renderer and HLSL shaders.
- `native/GPUTest.Physics`: small C ABI bridge around PhysX. Calls are batched, not per-ball P/Invoke. Standard GPU mode preserves host pose updates and speculative CCD.
- `scripts`: dependency restoration and builds. Native output is copied next to the managed executable.
- `tests/GPUTest.Tests`: xUnit tests, including actual GPU physics tests.
- `.deps` and `artifacts`: ignored SDK downloads, builds, benchmarks and captures.

PhysX is pinned to tag `106.1-physx-5.4.2`, commit `19542b61c4fee8e5aaa97f10455933488383d092`, with NVIDIA's matching `5.4.2.e5f54faf-release-106.1-windows-public` GPU runtime. This differs from the planning baseline of 5.8: 5.8 requires building GPU kernels, whereas this version supplies an official prebuilt Windows GPU runtime. PhysX source is built locally against that matching runtime. Vortice.Direct3D12 and Vortice.Dxc are pinned to 3.8.3; NuGet lock files are included. PhysX's BSD license is copied with the runtime. Third-party libraries retain their respective licenses; this is not yet a redistributable release package.

## Next Gate

Profile PhysX broadphase, contacts, constraint solve, speculative CCD and host synchronization separately. The combined measurement shows physics dominates; optimizations to glass or GPU region splitting cannot recover that budget. Retest correctness and the same benchmark after each physics change.

The direct-GPU PhysX API is a possible later optimization, but it disables standard CCD and CPU pose APIs. It must not be enabled without redesigning/testing fast-motion containment and pose transfer. Reducing solver fidelity, replacing rigid bodies with particles, or lowering the required 100k count requires an explicit decision.

Remaining rendering work includes full-frame glass/depth composition, reflections/refraction, lighting/shadows, snapshot interpolation, pipelined frame resources, dynamic region balancing, adapter/display/CUDA identity mapping and GPU timing captures. The prototype selects the first two NVIDIA DXGI adapters and lets PhysX select its CUDA device; it does not yet force physics onto the non-display GPU. It waits for each composed frame for resource safety and is not a throughput-optimized renderer. Long-duration tests, one-versus-two GPU comparisons, publish validation and device-loss recovery are still outstanding.