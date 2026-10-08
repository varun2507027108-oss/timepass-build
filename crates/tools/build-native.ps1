# Builds the native (Windows) Rust plugin and installs it for the Unity Editor
# and the Windows player.
#
# This is the counterpart to build-wasm.ps1. The Editor and the Windows
# standalone player load war_core_ffi.dll directly via P/Invoke; WebGL uses the
# .wasm path instead.
#
# Usage:
#   pwsh crates/tools/build-native.ps1
#   pwsh crates/tools/build-native.ps1 -DebugBuild

[CmdletBinding()]
param(
    [switch]$DebugBuild
)

$ErrorActionPreference = 'Stop'

$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$CratesDir = Join-Path $RepoRoot 'crates'
$Profile   = if ($DebugBuild) { 'dev' } else { 'release' }
$TargetDir = if ($DebugBuild) { 'debug' } else { 'release' }

Write-Host "==> building war-core-ffi for x86_64-pc-windows-msvc ($Profile)" -ForegroundColor Cyan

# cargo writes progress to stderr, which PowerShell reports as error records.
# Relax the preference around the native call and trust $LASTEXITCODE.
$ErrorActionPreference = 'Continue'
& cargo build `
    --manifest-path (Join-Path $CratesDir 'Cargo.toml') `
    -p war-core-ffi `
    "--profile=$Profile" `
    --locked `
    2>&1 | ForEach-Object { Write-Host $_ }
$cargoExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'

if ($cargoExit -ne 0) {
    throw "cargo build failed with exit code $cargoExit"
}

$Built = Join-Path $CratesDir "target\$TargetDir\war_core_ffi.dll"
if (-not (Test-Path -LiteralPath $Built)) {
    throw "expected artefact not found: $Built"
}

# Verify the C symbols are actually exported and unmangled. A Rust build that
# silently stopped exporting extern "C" symbols produces a DLL that loads fine and
# then fails at the first P/Invoke, so check before installing.
Write-Host "`n==> checking exported C symbols" -ForegroundColor Cyan
$text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($Built))
$required = @(
    'wa_spike_abi_version', 'wa_spike_state_slots', 'wa_spike_state_layout_slots',
    'wa_spike_create', 'wa_spike_destroy', 'wa_spike_advance',
    'wa_spike_read', 'wa_spike_feed', 'wa_spike_echo'
)
$missing = @()
foreach ($symbol in $required) {
    if ($text.Contains($symbol)) {
        Write-Host "    $symbol" -ForegroundColor DarkGray
    }
    else {
        Write-Host "    $symbol  MISSING" -ForegroundColor Red
        $missing += $symbol
    }
}
if ($missing.Count -gt 0) {
    throw "the following C symbols are not exported: $($missing -join ', ')"
}

$InstallDir = Join-Path $RepoRoot 'Assets\Plugins\x86_64'
if (-not (Test-Path -LiteralPath $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item -LiteralPath $Built -Destination (Join-Path $InstallDir 'war_core_ffi.dll') -Force

$size = (Get-Item -LiteralPath (Join-Path $InstallDir 'war_core_ffi.dll')).Length
Write-Host "`n==> installed war_core_ffi.dll ($([math]::Round($size / 1KB, 1)) KiB)" -ForegroundColor Green
Write-Host "    $InstallDir"
Write-Host "`n    Restart the Unity Editor to pick up the new plugin." -ForegroundColor Yellow