# Builds the WebGL WebAssembly plugin and verifies it outside Unity.
#
# Why this script exists: a Rust -> Unity WebGL plugin cannot be built by Unity's
# own pipeline, so it is easy to ship a stale or broken .wasm next to a fresh
# C# shell and spend an hour debugging the wrong artefact. This script always
# rebuilds, always re-verifies, and only then copies into Assets/.
#
# Usage:
#   pwsh crates/tools/build-wasm.ps1              # build + verify + install
#   pwsh crates/tools/build-wasm.ps1 -SkipInstall # verify only
#   pwsh crates/tools/build-wasm.ps1 -DebugBuild  # unoptimised build
#
# The flag is -DebugBuild and not -Debug because PowerShell reserves -Debug as a
# common parameter for any [CmdletBinding()] script.

[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$DebugBuild
)

$ErrorActionPreference = 'Stop'

$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$CratesDir = Join-Path $RepoRoot 'crates'
$TargetDir = if ($DebugBuild) { 'debug' } else { 'release' }
$Profile   = if ($DebugBuild) { 'dev' } else { 'release' }

# The generated .wasm must land in Assets/StreamingAssets so it is copied into
# the WebGL build folder verbatim and is fetchable by URL at runtime. It must NOT
# live in Assets/Plugins/WebGL: Unity's WebGL plugin linker only accepts
# Emscripten-produced .a/.bc archives (or modules it can dynamically link), and
# this module defines its own memory, so it is neither. See PORT-NOTES.md.
$InstallDir = Join-Path $RepoRoot 'Assets\StreamingAssets\war_core'
$WasmName   = 'war_core.wasm'

Write-Host "==> building war-core-ffi for wasm32-unknown-unknown ($Profile)" -ForegroundColor Cyan

# These target features must match what Unity 6's WebGL player is built with, or
# the two modules disagree at instantiation. Unity 6 ships Emscripten 4.0.20 and
# targets WebAssembly 2023.
$previousRustflags = $env:RUSTFLAGS
$env:RUSTFLAGS = '-C target-feature=+simd128,+bulk-memory,+nontrapping-fptoint,+sign-ext'

try {
    # `cargo` writes its progress lines to stderr, which PowerShell surfaces as
    # error records. With $ErrorActionPreference = 'Stop' that would abort on the
    # word "Compiling", so relax it around the native call and judge success by
    # $LASTEXITCODE instead -- which is the only reliable signal anyway.
    $ErrorActionPreference = 'Continue'
    & cargo build `
        --manifest-path (Join-Path $CratesDir 'Cargo.toml') `
        -p war-core-ffi `
        --target wasm32-unknown-unknown `
        "--profile=$Profile" `
        --locked `
        2>&1 | ForEach-Object { Write-Host $_ }
    $cargoExit = $LASTEXITCODE
}
finally {
    $env:RUSTFLAGS = $previousRustflags
    $ErrorActionPreference = 'Stop'
}

if ($cargoExit -ne 0) {
    throw "cargo build failed with exit code $cargoExit"
}

$BuiltWasm = Join-Path $CratesDir "target\wasm32-unknown-unknown\$TargetDir\war_core_ffi.wasm"
if (-not (Test-Path -LiteralPath $BuiltWasm)) {
    throw "expected artefact not found: $BuiltWasm"
}

Write-Host "`n==> verifying the module outside Unity (node)" -ForegroundColor Cyan
$ErrorActionPreference = 'Continue'
& node (Join-Path $CratesDir 'tools\wasm-interop-probe.mjs') $BuiltWasm 2>&1 |
    ForEach-Object { Write-Host $_ }
$probeExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'

if ($probeExit -ne 0) {
    throw "wasm interop probe FAILED (exit $probeExit) - refusing to install a broken artefact"
}

if ($SkipInstall) {
    Write-Host "`n==> -SkipInstall set; not copying into Assets/" -ForegroundColor Yellow
    Write-Host "    built: $BuiltWasm"
    exit 0
}

Write-Host "`n==> installing into $InstallDir" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item -LiteralPath $BuiltWasm -Destination (Join-Path $InstallDir $WasmName) -Force

$size = (Get-Item -LiteralPath (Join-Path $InstallDir $WasmName)).Length
Write-Host "    $WasmName  ($([math]::Round($size / 1KB, 1)) KiB)" -ForegroundColor Green
Write-Host "`n==> done" -ForegroundColor Green