param([switch]$SkipRestore, [switch]$Profile)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $sdk = Join-Path $root '.deps/physx-5.4.2'
    $commit = '19542b61c4fee8e5aaa97f10455933488383d092'
    if (-not (Test-Path "$sdk/physx/include/PxPhysicsAPI.h")) {
        & git clone --depth 1 --branch 106.1-physx-5.4.2 https://github.com/NVIDIA-Omniverse/PhysX.git $sdk
        if ($LASTEXITCODE -ne 0) { throw 'PhysX clone failed.' }
    }
    $actualCommit = & git -C $sdk rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $commit) { throw 'Unexpected PhysX revision.' }
    if (-not $SkipRestore) {
        & "$sdk/physx/buildtools/packman/packman.cmd" pull "$sdk/physx/dependencies.xml" --platform vc17win64
        if ($LASTEXITCODE -ne 0) { throw 'PhysX dependency restore failed.' }
    }
    $cache = $env:PM_PACKAGES_ROOT
    if (-not $cache) { $cache = [Environment]::GetEnvironmentVariable('PM_PACKAGES_ROOT', 'User') }
    if (-not $cache) { throw 'Packman package cache not found. Run without -SkipRestore.' }
    $vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
    if (-not $installation) { throw 'Install Visual Studio C++ Build Tools and the Windows SDK.' }
    $cmake = Join-Path $installation[0].installationPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    $major = ([version]$installation[0].installationVersion).Major
    $generator = if ($major -ge 18) { 'Visual Studio 18 2026' } else { 'Visual Studio 17 2022' }
    $gpu = "$cache/chk/PhysXGpu/5.4.2.e5f54faf-release-106.1-windows-public/bin"
    & $cmake -S native/GPUTest.Physics -B artifacts/native -G $generator -A x64 `
        "-DPHYSX_SLN_PHYSXDEVICE_PATH=$cache/chk/PhysXDevice/18.12.7.6/bin/x86" `
        "-DPHYSX_SLN_FREEGLUT_PATH=$cache/chk/freeglut-windows/3.4_1.1/bin" `
        "-DPHYSX_SLN_PHYSXGPU_PATH=$gpu" -DGPU_DLL_COPIED=0 "-DGPUTEST_PROFILE_ZONES=$($Profile.IsPresent)"
    if ($LASTEXITCODE -ne 0) { throw 'Native CMake configuration failed.' }
    & $cmake --build artifacts/native --config release --target GPUTest.Physics --parallel 4
    if ($LASTEXITCODE -ne 0) { throw 'Native compilation failed.' }
    Copy-Item "$gpu/win.x86_64.vc142.mt/release/PhysXGpu_64.dll" artifacts/native/runtime/release/ -Force
    Copy-Item "$cache/chk/PhysXDevice/18.12.7.6/bin/x86/PhysXDevice64.dll" artifacts/native/runtime/release/ -Force
    Copy-Item "$sdk/LICENSE.md" artifacts/native/runtime/release/PhysX-LICENSE.md -Force
}
finally { Pop-Location }