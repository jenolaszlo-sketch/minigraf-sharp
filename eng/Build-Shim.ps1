<#
.SYNOPSIS
Builds, hashes, and stages the owned prepared-query shim (native/shim) for win-x64.

.DESCRIPTION
M3.1a pipeline (docs/m3-1a-shim-abi-proposal.md):
  1. Verifies the Rust toolchain is present (cargo, rustc, cbindgen).
  2. Regenerates native/include/minigrafsharp_shim.h with cbindgen.
  3. cargo build --release --locked from native/shim.
  4. Hashes + stages native/staging/win-x64/minigrafsharp_shim.dll.
  5. Refreshes the shim blocks in native/upstream.json and the staging manifest.
The `qualified` flags are NEVER flipped by this script; they flip only in the
evidence step after the qualification rerun is green.

.NOTES
Toolchain requirement: cargo/rustc on PATH (or $env:CARGO_HOME\bin), cbindgen installed
via `cargo install cbindgen`. This script changes tracked files; commit the refreshed
manifests with the slice.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$shimDir = Join-Path $repoRoot "native\shim"
$headerPath = Join-Path $repoRoot "native\include\minigrafsharp_shim.h"
$stagingDir = Join-Path $repoRoot "native\staging\win-x64"
$upstreamPath = Join-Path $repoRoot "native\upstream.json"

function Resolve-Tool($name, $fallbackDir) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { return $cmd.Source }
    $guess = Join-Path $fallbackDir "$name.exe"
    if (Test-Path $guess) { return $guess }
    throw "Tool '$name' not found on PATH or at $guess"
}

$cargo = Resolve-Tool "cargo" (Join-Path $env:USERPROFILE ".cargo\bin")
$cbindgen = Resolve-Tool "cbindgen" (Join-Path $env:USERPROFILE ".cargo\bin")
$cargoDir = Split-Path $cargo
$env:PATH = "$cargoDir;$env:PATH"
$rustc = Resolve-Tool "rustc" $cargoDir

$cargoVersion = (& $cargo --version) -join " "
$rustcVersion = (& $rustc --version) -join " "
$cbindgenVersion = (& $cbindgen --version) -join " "
Write-Host "[toolchain] $cargoVersion | $rustcVersion | $cbindgenVersion"

# 1. Regenerate the header.
try {
    Push-Location $shimDir
    & $cbindgen --config cbindgen.toml --crate "minigrafsharp-shim" --output $headerPath 2>&1 | ForEach-Object { Write-Verbose $_ }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $headerPath)) { throw "cbindgen failed" }
}
finally { Pop-Location }
$enc = [System.Text.UTF8Encoding]::new($false, $true)
$null = $enc.GetString([System.IO.File]::ReadAllBytes($headerPath)) # UTF-8 validity gate
if (([System.IO.File]::ReadAllBytes($headerPath) -contains 13) ) { throw "header must be LF-only" }
$headerBytes = (Get-Item $headerPath).Length
$headerSha = (Get-FileHash $headerPath -Algorithm SHA256).Hash
Write-Host "[header] sha256=$headerSha bytes=$headerBytes"

# 2. Locked release build.
Push-Location $shimDir
try {
    & $cargo build --release --locked
    if ($LASTEXITCODE -ne 0) { throw "cargo build --release --locked failed" }
}
finally { Pop-Location }

$dll = Join-Path $shimDir "target\release\minigrafsharp_shim.dll"
if (-not (Test-Path $dll)) { throw "shim DLL missing at $dll" }
$dllSha = (Get-FileHash $dll -Algorithm SHA256).Hash
$dllBytes = (Get-Item $dll).Length
Write-Host "[binary] sha256=$dllSha bytes=$dllBytes"

# 3. Stage binary.
if (-not (Test-Path $stagingDir)) { throw "staging dir missing: $stagingDir" }
Copy-Item $dll (Join-Path $stagingDir "minigrafsharp_shim.dll") -Force

# 4. Read provenance sources.
$upstream = Get-Content $upstreamPath -Raw | ConvertFrom-Json
$coreVersion = $upstream.minigraf.version
$coreCommit = $upstream.minigraf.commit
$identity = Get-Content (Join-Path $shimDir "identity.json") -Raw | ConvertFrom-Json
$shimAbi = $identity.shim_abi
$shimRevision = $identity.source_revision

# 5. Refresh staging manifest.
$crateSha = $upstream.shim.crate.pinned_minigraf.tarball_sha256
$crateBytes = $upstream.shim.crate.pinned_minigraf.tarball_bytes
$manifest = [ordered]@{
    rid = "win-x64"
    artifact = "minigrafsharp_shim.dll"
    binary_sha256 = $dllSha
    binary_bytes = $dllBytes
    shim_abi = $shimAbi
    shim_source_revision = $shimRevision
    core_version = $coreVersion
    core_commit = $coreCommit
    toolchain = [ordered]@{
        rustc = $rustcVersion
        cargo = $cargoVersion
        cbindgen = $cbindgenVersion
    }
    crate_pin = [ordered]@{
        version = $coreVersion
        tarball_sha256 = $crateSha
        tarball_bytes = $crateBytes
    }
    qualified = $false
}

# BOM-less UTF-8 (PowerShell 5.1's Set-Content -Encoding UTF8 adds a BOM,
# which consumers parsing the raw bytes reject).
function Write-Utf8NoBom([string]$Path, [string]$Text)
{
    [System.IO.File]::WriteAllText(
        (Resolve-Path -LiteralPath (Split-Path -Parent $Path)).Path + '\' + (Split-Path -Leaf $Path),
        $Text,
        [System.Text.UTF8Encoding]::new($false))
}

Write-Utf8NoBom (Join-Path $stagingDir "minigrafsharp-shim.json") ($manifest | ConvertTo-Json -Depth 5)

# 6. Refresh tracked provenance (upstream.json shim block).
$upstream.shim.header.sha256 = $headerSha
$upstream.shim.header.bytes = $headerBytes
$upstream.shim.toolchain.rustc = $rustcVersion
$upstream.shim.toolchain.cargo = $cargoVersion
$upstream.shim.toolchain.cbindgen = $cbindgenVersion
$upstream.shim.assets."win-x64".binary_sha256 = $dllSha
$upstream.shim.assets."win-x64".binary_bytes = $dllBytes
Write-Utf8NoBom $upstreamPath ($upstream | ConvertTo-Json -Depth 10)

Write-Host "[staged] minigrafsharp_shim.dll staged; provenance refreshed (qualified flags untouched)."
