# GPUTest

C# / .NET 10 Windows desktop **feasibility prototype** for rigid-body physics and rendering on a single NVIDIA GPU.

## Status

Implemented: PhysX GPU sphere collisions, a mouse-translated/rotated six-wall kinematic container, 0-100,000 balls, pause/step/reset, and full-width D3D12 rendering on the same physical GPU as physics. A dedicated worker owns physics; the UI consumes complete reusable snapshots. PhysX selects its CUDA device, and the renderer matches that device's Windows LUID to a DXGI adapter. Missing or incompatible adapter identity is an error, not a fallback to another GPU. Multi-GPU rendering, split viewports, cross-adapter resources and cross-adapter synchronization have been removed. Three fenced resource slots allow CPU/GPU overlap without a per-frame queue drain. Diagnostic captures explicitly use readback; normal rendering does not.

**Not finished:** the cube is currently wireframe, not optically shaded glass. The 100,000-ball real-time performance requirement is **not met**. This milestone intentionally stops short of visual polish because the physics feasibility gate failed. No lower ball count or CPU renderer is silently substituted.

## Run

On this workstation, dependencies have already been restored and the native bridge built:

```powershell
dotnet run --project src/GPUTest.App -c Release
```

Normal launches start with 1,000 balls. Explicit counts and diagnostic defaults are unchanged.

To start directly with ten balls:

```powershell
dotnet run --project src/GPUTest.App -c Release -- --balls 10
```

In VS Code, select **GPUTest prototype** and press F5. The build task restores dependencies, builds the native bridge, and builds the .NET solution. The C# extension is required for debugging; extension recommendations are included.

First build on another machine:

```powershell
./scripts/build.ps1
```

Prerequisites: Windows x64, .NET SDK 10.0.401 (or a later patch in that feature band), one NVIDIA GPU supporting CUDA and D3D12 in WDDM mode, a compatible NVIDIA driver, Git with Git LFS, and Visual Studio 2022/2026 C++ Build Tools including CMake and a Windows SDK. A CUDA Toolkit installation is not needed for this pinned prebuilt GPU runtime. Additional installed GPUs are not used for application physics or rendering.

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

Mouse translation uses the camera's projected scale at the cube's current depth, so cursor and target displacement match on screen even after resizing. **All scenes, from 0 to 100,000 balls, apply the latest translation and rotation target on the next physics tick.** There is no ball-count cutoff, translation-speed cap, acceleration easing or rotation-speed cap. Cube center remains limited to +/-4 m along each world axis. The camera is fixed for now. Removing the dense-scene limits does not make physics faster: earlier unrestricted dense-scene trials failed progress or overflow checks, and heavy scenes can still slow down or stop with a collision error.

Large wall movements and fast balls trigger additional PhysX substeps within the same 1/120-second outer tick. Substep duration is bounded by relative wall/ball travel over the 0.5 m wall thickness, with velocity rechecked after each substep. No balls are teleported with the cube, and the renderer still displays cube and balls from the same completed physical state. Solver iterations, friction, restitution and speculative CCD are unchanged. Arbitrary extreme motion or a heavily loaded GPU can still exceed the simulation budget; this is not a zero-latency or universal containment guarantee.

The cube is 10 m wide internally and all balls have radius 0.08 m. Counts do not silently change sphere size. Balls start in an ordered, non-overlapping bottom-up lattice. The physics worker has an 8 ms catch-up budget and permits at least one due step; an individual native step can exceed that budget without blocking UI input. Reset, pause, step and target requests are applied between native calls. Pause can therefore finish an already-running step before taking effect. Minimize suspends new steps; closing asynchronously waits for physics disposal on its owner thread.

When physics cannot keep up, the app drops excess wall-clock catch-up time, keeps the 1/120-second outer tick (subdivided internally when needed), and displays **Slow motion (physics overload)**. The status also shows simulation speed (`sim 1.00x` means wall-clock speed), separately from render FPS. Ball count, solver settings and collision detection are unchanged. This is an explicit slow-motion fallback, not achievement of the real-time target. Unchanged cube targets, including quaternion-sign-equivalent rotations, are no longer resubmitted, allowing the stationary kinematic actor to sleep.

Rendering polls frame fences and presentation readiness without waiting on the UI thread. A busy frame slot leaves the previous image displayed. Each frame uses one full-width scene target and a local copy into the swap chain, submitted in one command list on one queue. Resource slots are reused only after their completion fence signals. Resize still rebuilds the renderer synchronously (performance review finding 5, not addressed here). Severe runtime errors require restarting; a hung driver or native call cannot be forcibly interrupted safely.

Simulation shapes do not participate in PhysX scene queries: the application does not use raycasts, sweeps or overlap queries. This removes unused query-index maintenance, not physical collisions. Sphere geometry, contact offsets, outer timestep, solver iterations, speculative CCD, friction and restitution are unchanged. The CPU dispatcher still uses four workers; increasing it to eight was slower in the measured trace.

## Validation

```powershell
dotnet test GPUTest.slnx -c Release
dotnet run --project src/GPUTest.App -c Release -- --probe
dotnet run --project src/GPUTest.App -c Release -- --physics-probe
dotnet run --project src/GPUTest.App -c Release -- --render-probe
dotnet run --project src/GPUTest.App -c Release -- --render-probe --gpu-validation
dotnet run --project src/GPUTest.App -c Release -- --ui-smoke
dotnet run --project src/GPUTest.App -c Release -- --ui-smoke 10
dotnet run --project src/GPUTest.App -c Release -- --ui-smoke 1000
dotnet run --project src/GPUTest.App -c Release -- --ui-smoke 1001
dotnet run --project src/GPUTest.App -c Release -- --physics-benchmark 100000 120
dotnet run --project src/GPUTest.App -c Release -- --benchmark 100000 120
```

Thirty-three test cases cover native pose layout, invalid ball/worker counts, direct next-tick tracking, target coalescing, fast drag/reversal response, screen-space drag mapping, overload scheduling/recovery, cube sleep/wake, GPU scene destruction/recreation, settling and moving/rotating containment. Worker tests cover commands during a blocked step, stable snapshots, reset generations and their GPU identity, queued single-steps, failure propagation and shutdown without disposing an in-flight world. Direct worker tracking is tested at 10, 1,000, 1,001, 10,000 and 100,000 balls using a test world; this verifies input routing, not real GPU throughput at those counts. Native recreation tests require a nonzero adapter LUID. Additional tests compare physical trajectories with query participation on/off and exercise repeated profiling start/stop. The rapid-motion containment test covers 10, 1,000 and 10,000 balls with direct controller targets. GPU tests require the native runtime and an NVIDIA GPU and are not silently skipped.

The adapter probe creates one D3D12 device on the PhysX GPU and reports its matching LUID. The finite rendering probe opens a window, renders 30 frames across all resource slots, and checks scene pixels in both halves of an actual GPU backbuffer capture. Both image halves come from the same rendering device. It writes `artifacts/render-probe.png`, independent of desktop occlusion. The 385-frame UI smoke test defaults to 10,000 balls (optional count argument), injects an initial translation/rotation through the mouse handler, exercises sustained motion and deliberately blocks a physics step until a UI callback releases it. That callback must run within 250 ms; continued physics, resizing, count reset, pause and exactly one requested step are also required. It writes a viewport capture to `artifacts/ui-smoke.png`. Small-scene runs additionally require the initial mouse-handler target to reach a matching render submission within 100 ms. These are not hardware input-to-photon measurements, exhaustive mouse-input tests or long soak tests.

The optional `--gpu-validation` switch enables the D3D12 debug layer and checks the rendering device for error/corruption messages at drain points. It requires the Windows Graphics Tools optional component and fails explicitly if unavailable. It was **not executable on this workstation** (`DXGI_ERROR_SDK_COMPONENT_MISSING`); normal GPU probes passed, but debug-layer validation and a GPU timeline capture remain unverified. Use `dotnet run` or the built application executable, not `dotnet GPUTest.App.dll`, so PhysX can find its companion GPU DLLs.

Benchmark arguments are ball count and measured frame count. Benchmarks warm up for 120 frames and use a fixed translation/rotation trace with two 1/120-second physics steps per rendered frame. The combined benchmark renders at 2560x1440 and includes the final GPU drain in its measurements. It intentionally performs physics synchronously to measure full fixed-step throughput, not the interactive worker's slow-motion fallback. A completed measurement exits successfully even when the printed performance gate is NOT MET. An initialization, containment, native warning, or rendering failure exits unsuccessfully. These short runs are feasibility measurements, not the planned long-duration acceptance suite.

### Profiling

Before comparing results, check `nvidia-smi` for competing workloads and available memory. A loaded Ollama model can occupy VRAM even when it is idle. Do not unload another application's model without permission. The measurements in this session exposed roughly 22 GB occupied on each GPU by a loaded `gpt-oss:120b` model and other applications while GPUTest was closed. That leaves little room for the simulation and can distort both GPU execution and presentation timings.

The physics benchmark separates two simulation steps from pose copying/validation. `--workers` selects 0-64 CPU dispatcher workers for that benchmark only; `--queries` restores the original scene-query participation for comparison. Supply ball and frame counts before these options:

```powershell
dotnet run --project src/GPUTest.App -c Release -- --physics-benchmark 100000 120 --workers 4 --queries
dotnet run --project src/GPUTest.App -c Release -- --physics-benchmark 100000 120 --workers 4
```

The combined benchmark separately reports physics/pose time and renderer submission/waits/presentation time. These are host wall times, not GPU kernel timings.

Optional native host-zone profiling requires an instrumented build:

```powershell
./scripts/build-native.ps1 -SkipRestore -Profile
dotnet run --project src/GPUTest.App -c Release -- --physics-benchmark 100000 120 --profile
```

`--profile` collects synchronous PhysX host zones only during the measured interval and prints the 20 largest inclusive totals. Nested and parallel zones overlap and must not be summed. Detached zones are omitted. GPU contact/patch/pair buffer statistics are lifetime high-water values, including warmup; ordinary contact counters may not describe GPU contacts. Profiling adds overhead. A normal native build has no host zones; `--profile` still reports scene/buffer counters and explicitly says that zones are unavailable.

Restore the normal build before final timing:

```powershell
./scripts/build-native.ps1 -SkipRestore
dotnet build GPUTest.slnx -c Release
```

## Measured Results

### Motion Limits Removed (2026-10-12)

The ball-count-based controller limits have been removed at the user's request. All 33 tests pass. The 1,001-ball GPU UI smoke test passes above the former cutoff, including a 0.9 ms callback during a blocked physics step and 653 subsequent steps, sustained motion, resize, reset, pause and single-step. Collision substeps, error detection and the world-position boundary remain active. No large-scene real-time performance claim is made; the existing heavy-scene progress and overflow checks have not been relaxed.

### Historical Small-Scene Mouse Fix (2026-10-12)

The old controller advanced only 0.0054 m toward a 2 m target on its first tick, even with no physics load. The new direct-tracking regression reaches the exact target on the next tick. All 29 tests pass. UI smoke runs measured 31.1 ms with 10 balls and 74.9 ms with 1,000 balls from mouse-handler input to submission of the matching physical snapshot; both completed sustained movement, resize, reset, pause and single-step checks. The 10,000-ball compatibility run retained its motion limits and passed with a 12.7 ms blocked-step callback and 301 subsequent physics steps. These timings are individual initial-drag samples, not latency percentiles or display scanout timings.

The throughput measurements below predate the mouse fix and adaptive collision substeps; they are retained as historical baselines, not remeasured performance claims for the new input path.

Windows 10.0.26200, .NET SDK 10.0.401, NVIDIA driver 617.42, two RTX 4090s. Adapter LUIDs during this session were 113200 and 119357; these IDs are not persistent device identifiers.

### Current Single-GPU Results (2026-10-12)

CPU: AMD Ryzen Threadripper PRO 7965WX, 24 cores / 48 logical processors. Ollama's loaded model was gracefully unloaded before these runs. Immediately beforehand, the simulation GPU reported 0 MiB used, 24,138 MiB free and 0% utilization; the display GPU reported 2,744 MiB used and 8% utilization. Ordinary desktop applications remained running. Profiling zones were disabled and the dispatcher used four workers unless stated otherwise. Physics and rendering selected the same LUID 119357.

Each run used 120 warmup frames and 120 measured frames. All traces completed with zero escaped balls and no native warnings:

| Workload | Average | p95 | Result |
| --- | ---: | ---: | --- |
| 100,000 balls, physics + poses, queries on | 78.18 ms | 85.20 ms | Original query participation |
| 100,000 balls, physics + poses, queries off | 74.00 ms | 80.20 ms | Current default |
| 100,000 balls, physics + poses, queries on, repeat | 77.39 ms | 84.68 ms | Baseline recheck |
| 100,000 balls, physics + poses, queries off, eight workers | 78.29 ms | 83.74 ms | Slower than four workers |
| 100,000 balls, physics + rendering at 1440p | 74.26 ms | 83.11 ms | 13.47 FPS; realtime-factor 0.22; 60 FPS gate NOT MET |
| 10,000 balls, physics + rendering at 1440p | 10.29 ms | 11.48 ms | 97.17 FPS; realtime-factor 1.55; short-run 60 FPS gate MET |

Removing unused query indexing reduced physics/pose time by approximately 4-5% versus the two neighboring baseline runs. In the combined 100k run, physics/poses took 73.44 ms and renderer submission/waits/presentation took 0.82 ms per frame. At 10k those stages took 9.76 ms and 0.53 ms. These are host wall times; GPU rendering can overlap other work. Unloading Ollama did not solve the 100k physics bottleneck, although the large presentation waits seen in earlier 10k runs were absent. The smaller-count result does not satisfy the required 100k target.

All 25 tests passed. The UI smoke callback completed in 12.8 ms during a blocked physics step, followed by 319 steps; motion, resize, count reset, pause and exact single-step passed. The 30-frame GPU capture passed pixel checks in both halves and was visually inspected. Collision/solver settings and mouse-motion limits remain unchanged.

### Initial Single-GPU Conversion

Physics and rendering both selected LUID 119357 (CUDA device 0); no rendering device was created on LUID 113200. All 21 tests passed. The UI smoke callback completed in 6.4 ms during the blocked worker step, followed by 314 physics steps; resize, reset, pause and exact single-step passed. The 30-frame capture contained scene pixels in both image halves.

After 120 warmup frames and 120 measured frames:

| Workload | Average | p95 | Result |
| --- | ---: | ---: | --- |
| 100,000 balls, physics + single-GPU rendering at 1440p | 73.68 ms | 80.42 ms | 13.57 FPS; realtime-factor 0.22; no escapes; 60 FPS gate NOT MET |

These short runs validate the single-GPU path, not a controlled performance comparison with the previous renderer. Mouse-motion limits and physics quality settings are unchanged.

### Historical Dual-GPU Results

The following measurements used the removed dual-GPU renderer; they do not describe the current rendering architecture.

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

That review-fix UI smoke callback completed in 20.2 ms during the blocked worker step, followed by 316 physics steps. All 21 tests passed. Those measurements did not demonstrate a throughput improvement over the initial run; physics remained the main limit. Different-day runs are not a controlled A/B comparison, and actual GPU overlap duration was not profiled. Those fixes addressed ownership, UI blocking and submission dependencies without claiming the 100k real-time requirement was met.

Physics warnings are treated as errors. The initial 100k run identified insufficient `foundLostPairsCapacity`; the corrected bridge allocates four million pair entries and completes the recorded trace without the overflow. More extreme motion may require further capacity/containment work.

## Implementation

- `src/GPUTest.App`: WinForms host, bounded cube controller, native interop, D3D12 renderer and HLSL shaders.
- `native/GPUTest.Physics`: small C ABI bridge around PhysX. Calls are batched, not per-ball P/Invoke. Standard GPU mode preserves host pose updates and speculative CCD.
- `scripts`: dependency restoration and builds. Native output is copied next to the managed executable.
- `tests/GPUTest.Tests`: xUnit tests, including actual GPU physics tests.
- `.deps` and `artifacts`: ignored SDK downloads, builds, benchmarks and captures.

PhysX is pinned to tag `106.1-physx-5.4.2`, commit `19542b61c4fee8e5aaa97f10455933488383d092`, with NVIDIA's matching `5.4.2.e5f54faf-release-106.1-windows-public` GPU runtime. This differs from the planning baseline of 5.8: 5.8 requires building GPU kernels, whereas this version supplies an official prebuilt Windows GPU runtime. PhysX source is built locally against that matching runtime. Vortice.Direct3D12 and Vortice.Dxc are pinned to 3.8.3; NuGet lock files are included. PhysX's BSD license is copied with the runtime. Third-party libraries retain their respective licenses; this is not yet a redistributable release package.

## Next Gate

The isolated combined run confirms that physics/pose work dominates at 100k; rendering-only optimization cannot recover the required budget. The host profile suggests contact-island maintenance and speculative contact-distance updates as areas to investigate, but that instrumented capture predates workload isolation and its inclusive zone totals are not GPU timings. Obtain a GPU/CPU timeline before choosing a larger simulation change. Keep future comparisons free of competing GPU-memory workloads, and retest correctness with the same trace after each change. Direct tracking is now enabled for all counts by request; further work is needed on dense-scene fast-motion throughput and containment. Do not hide lag by moving the displayed cube ahead of its physical state.

The direct-GPU PhysX API is a possible later optimization, but it disables standard CCD and CPU pose APIs. It must not be enabled without redesigning/testing fast-motion containment and pose transfer. Reducing solver fidelity, replacing rigid bodies with particles, or lowering the required 100k count requires an explicit decision.

Remaining rendering work includes glass shading, reflections/refraction, lighting/shadows, snapshot interpolation, resize resource reuse and GPU timing captures on the single selected adapter. Long-duration tests, publish validation and device-loss recovery are still outstanding. Multi-GPU support is no longer part of the plan.