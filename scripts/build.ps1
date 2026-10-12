param([switch]$SkipNative, [switch]$SkipRestore)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if (-not $SkipNative) { & "$PSScriptRoot/build-native.ps1" -SkipRestore:$SkipRestore }
    if (-not (Test-Path 'artifacts/native/runtime/release/GPUTest.Physics.dll')) { throw 'Native bridge missing. Run the full build first.' }
    & dotnet build GPUTest.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }
}
finally { Pop-Location }