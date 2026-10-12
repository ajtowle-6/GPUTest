# GPUTest

C# / .NET 10 Windows desktop **feasibility prototype** for GPU rigid-body physics and explicit rendering on two NVIDIA GPUs.

## Status

Implemented: PhysX GPU sphere collisions, a mouse-translated/rotated six-wall kinematic container, 0-100,000 balls, pause/step/reset, and same-frame D3D12 rendering on two distinct adapters. A dedicated worker owns physics; the UI consumes complete reusable snapshots. Both GPUs render screen regions independently before composition; the secondary region travels through a shared buffer and fence into the primary swap chain. Three fenced resource slots allow CPU/GPU overlap without a per-frame queue drain. No rendered image is read back to the CPU for composition; diagnostic captures explicitly use readback.

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

The cube is 10 m wide internally and all balls have radius 0.08 m. Counts do not silently change sphere size. Balls start in an ordered, non-overlapping bottom-up lattice. The physics worker has an 8 ms catch-up budget and permits at least one due step; an individual native step can exceed that budget without blocking UI input. Reset, pause, step and target requests are applied between native calls. Pause can therefore finish an already-running step before taking effect. Minimize suspends new steps; closing asynchronously waits for physics disposal on its owner thread.

When physics cannot keep up, the app drops excess wall-clock catch-up time, keeps the 1/120-second timestep, and displays **Slow motion (physics overload)**. The status also shows simulation speed (`sim 1.00x` means wall-clock speed), separately from render FPS. Ball count, solver settings and collision detection are unchanged. This is an explicit slow-motion fallback, not achievement of the real-time target. Unchanged cube targets, including quaternion-sign-equivalent rotations, are no longer resubmitted, allowing the stationary kinematic actor to sleep.

Rendering polls frame fences and presentation readiness without waiting on the UI thread. A busy frame slot leaves the previous image displayed. Resource slots are reused only after primary composition has completed, which also guarantees the corresponding secondary export is finished. Primary scene submission precedes the secondary-image fence wait; only composition waits for that image. Resize still rebuilds the renderer synchronously (performance review finding 5, not addressed here). Severe runtime errors require restarting; a hung driver or native call cannot be forcibly interrupted safely.

## Validation

```powershell
dotnet test GPUTest.slnx -c Release
dotnet run --project src/GPUTest.App -c Release -- --probe
dotnet run --project src/GPUTest.App -c Release -- --physics-probe
dotnet run --project src/GPUTest.App -c Release -- --render-probe
dotnet run --project src/GPUTest.App -c Release -- --render-probe --gpu-validation
dotnet run --project src/GPUTest.App -c Release -- --ui-smoke
dotnet run --project src/GPUTest.App -c Release -- --physics-benchmark 100000 120
dotnet run --project src/GPUTest.App -c Release -- --benchmark 100000 120
```

Twenty-one test cases cover native pose layout, invalid counts, bounded control motion, fast drag/reversal response, screen-space drag mapping, overload scheduling/recovery, cube sleep/wake, GPU scene destruction/recreation, settling and moving/rotating containment. Worker tests cover commands during a blocked step, stable snapshots, reset generations, queued single-steps, failure propagation and shutdown without disposing an in-flight world. The rapid-motion containment test uses 10,000 balls. GPU tests require the native runtime and an NVIDIA GPU and are not silently skipped.

The finite rendering probe opens a window, renders 30 frames across all resource slots, and checks scene pixels in both halves of an actual composed-GPU-buffer capture. It writes `artifacts/render-probe.png`, independent of desktop occlusion. The 385-frame UI smoke test exercises sustained programmatic motion with 10,000 balls and deliberately blocks a physics step until a UI callback releases it. That callback must run within 250 ms; continued physics, resizing, count reset, pause and exactly one requested step are also required. It writes a viewport capture to `artifacts/ui-smoke.png`. These are not exhaustive mouse-input or long soak tests.

The optional `--gpu-validation` switch enables the D3D12 debug layer and checks both devices for error/corruption messages at drain points. It requires the Windows Graphics Tools optional component and fails explicitly if unavailable. It was **not executable on this workstation** (`DXGI_ERROR_SDK_COMPONENT_MISSING`); normal GPU probes passed, but debug-layer validation and a GPU timeline capture remain unverified. Use `dotnet run` or the built application executable, not `dotnet GPUTest.App.dll`, so PhysX can find its companion GPU DLLs.

Benchmark arguments are ball count and measured frame count. Benchmarks warm up for 120 frames and use a fixed translation/rotation trace with two 1/120-second physics steps per rendered frame. The combined benchmark renders at 2560x1440 and includes the final GPU drain in its measurements. It intentionally performs physics synchronously to measure full fixed-step throughput, not the interactive worker's slow-motion fallback. A completed measurement exits successfully even when the printed performance gate is NOT MET. An initialization, containment, native warning, or rendering failure exits unsuccessfully. These short runs are feasibility measurements, not the planned long-duration acceptance suite.

## Measured Results

Windows 10.0.26200, .NET SDK 10.0.401, NVIDIA driver 617.42, two RTX 4090s. Adapter LUIDs during this session were 113200 and 119357; these IDs are not persistent device identifiers.

Initial prototype measurements (2026-10-11):

| Workload | Average | p95 | Result |
| --- | ---: | ---: | --- |
| 10,000 balls, physics + pose copy | 9.23 ms | 10.72 ms | No escaped balls |
| 100,000 balls, physics + pose copy | 62.17 ms | 67.27 ms | No escaped balls; too slow |
| 100,000 balls, physics + two-GPU rendering at 1440p | 63.79 ms | 68.13 ms | 15.68 FPS; 60 FPS gate NOT MET |

After review findings 1-4 (2026-10-12), 120 measured frames after 120 warmup frames:

| Workload | Average | p95 | Result |
| --- | ---: | ---: | --- |
| 100,000 balls, physics + pose copy | 72.99 ms | 80.70 ms | No escaped balls; too slow |
| 100,000 balls, physics + two-GPU rendering at 1440p | 75.03 ms | 81.91 ms | 13.33 FPS; 60 FPS gate NOT MET |

The final UI smoke callback completed in 20.2 ms during the blocked worker step, followed by 316 physics steps. All 21 tests passed. The new measurements do not demonstrate a throughput improvement over the historical run; physics remains the main limit. Different-day runs are not a controlled A/B comparison, and actual GPU overlap duration has not been profiled. These fixes address ownership, UI blocking and submission dependencies without claiming the 100k real-time requirement is met.

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

Remaining rendering work includes full-frame glass/depth composition, reflections/refraction, lighting/shadows, snapshot interpolation, resize resource reuse, dynamic region balancing, adapter/display/CUDA identity mapping and GPU timing captures. The prototype selects the first two NVIDIA DXGI adapters and lets PhysX select its CUDA device; it does not yet force physics onto the non-display GPU. Long-duration tests, one-versus-two GPU comparisons, publish validation and device-loss recovery are still outstanding.