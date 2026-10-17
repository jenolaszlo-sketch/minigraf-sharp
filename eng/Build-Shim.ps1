<#
.SYNOPSIS
Builds, hashes, and stages the owned prepared-query shim (native/shim) for one host-RID.

.DESCRIPTION
M3.1a/M3.1c pipeline (docs/m3-1a-shim-abi-proposal.md,
docs/m3-1c-shim-packaging-proposal.md):
  1. Verifies the Rust toolchain is present (cargo, rustc, cbindgen).
  2. Regenerates native/include/minigrafsharp_shim.h with cbindgen and asserts
     it is byte-identical to the tracked copy (same source + pinned toolchain
     family must compose the same ABI header on every host).
  3. cargo build --release --locked from native/shim (host target only).
  4. Hashes + stages native/staging/<rid>/<shim binary>.
  5. Refreshes the shim blocks in native/upstream.json (per-RID hashes and
     toolchain records) and writes the per-RID staging manifest.
The `qualified` flags are NEVER flipped by this script; they flip only in the
evidence step after the qualification rerun is green.

.PARAMETER Rid
  RID to stage. Defaults to the host RID. Only the host combination may be
  staged; no cross-compilation in this pipeline.

.NOTES
Toolchain requirement: cargo/rustc on PATH (or $env:CARGO_HOME\bin), cbindgen installed
via `cargo install cbindgen@0.29.4 --locked`. This script changes tracked files; commit
the refreshed manifests with the slice.
#>
[CmdletBinding()]
param(
    [ValidateSet("win-x64", "linux-x64", "osx-arm64")]
    [string]$Rid = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
# Forward slashes everywhere: valid for .NET path APIs on Windows and Unix
# runners alike (backslash is a plain character on Unix).
$shimDir = Join-Path $repoRoot "native/shim"
$headerPath = Join-Path $repoRoot "native/include/minigrafsharp_shim.h"
$upstreamPath = Join-Path $repoRoot "native/upstream.json"

function Get-HostRid {
    $rt = [System.Runtime.InteropServices.RuntimeInformation]
    if ($rt::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { return "win-x64" }
    if ($rt::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        return "linux-x64"
    }
    if ($rt::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        return "osx-arm64"
    }
    throw "Unsupported OS for shim staging."
}

if ([string]::IsNullOrWhiteSpace($Rid)) { $Rid = Get-HostRid }
$hostRid = Get-HostRid
if ($Rid -ne $hostRid) {
    throw "Build-Shim stages the host combination only (host '$hostRid', requested '$Rid'). Qualified RIDs are built on their own runners; no cross builds."
}

# rid -> artifact name / layout, kept in lockstep with the loaders
# (<lib> prefix on non-Windows) and native/upstream.json shim.assets.
$ridLayout = @{
    "win-x64"   = @{ artifact = "minigrafsharp_shim.dll";     dir = "native/staging/win-x64" }
    "linux-x64" = @{ artifact = "libminigrafsharp_shim.so";   dir = "native/staging/linux-x64" }
    "osx-arm64" = @{ artifact = "libminigrafsharp_shim.dylib"; dir = "native/staging/osx-arm64" }
}
$artifact = $ridLayout[$Rid].artifact
$stagingDir = Join-Path $repoRoot $ridLayout[$Rid].dir

function Resolve-Tool($name, $fallbackDir) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { return $cmd.Source }
    # Fallback exists for Windows hosts only; runners put the toolchain on PATH.
    if (($PSVersionTable.PSVersion.Major -lt 6) -or $IsWindows) {
        $guess = Join-Path $fallbackDir "$name.exe"
        if (Test-Path $guess) { return $guess }
    }
    throw "Tool '$name' not found on PATH or at $fallbackDir"
}

if (($PSVersionTable.PSVersion.Major -lt 6) -or $IsWindows) {
    $cargoDirGuess = Join-Path $env:USERPROFILE ".cargo/bin"
} else {
    $cargoHome = $env:CARGO_HOME
    if (-not $cargoHome) { $cargoHome = Join-Path $env:HOME ".cargo" }
    $cargoDirGuess = Join-Path $cargoHome "bin"
}
$cargo = Resolve-Tool "cargo" $cargoDirGuess
$cbindgen = Resolve-Tool "cbindgen" $cargoDirGuess
$cargoDir = Split-Path $cargo
$env:PATH = "$cargoDir$([System.IO.Path]::PathSeparator)$env:PATH"
$rustc = Resolve-Tool "rustc" $cargoDir

$cargoVersion = (& $cargo --version) -join " "
$rustcVersion = (& $rustc --version) -join " "
$cbindgenVersion = (& $cbindgen --version) -join " "
Write-Host "[toolchain] $cargoVersion | $rustcVersion | $cbindgenVersion"

# 1. Regenerate the header.
try {
    Push-Location $shimDir
    # cbindgen warnings go to stderr; Windows PowerShell turns native stderr
    # into a terminating error under ErrorActionPreference=Stop, so capture
    # the output under Continue and judge success by exit code only.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $cbindgenOutput = & $cbindgen --config cbindgen.toml --crate "minigrafsharp-shim" --output $headerPath 2>&1
        $cbindgenExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousEap }
    foreach ($line in $cbindgenOutput) { Write-Host "$line" }
    if ($cbindgenExit -ne 0 -or -not (Test-Path $headerPath)) { throw "cbindgen failed ($cbindgenExit)" }
}
finally { Pop-Location }
# Canonicalize newlines: cbindgen output follows the host's line endings on
# some platforms; the pinned provenance identity is the LF blob, so the
# generated header is normalized before hashing.
$headerText = [System.IO.File]::ReadAllText($headerPath)
$headerText = $headerText.Replace("`r`n", "`n").Replace("`r", "`n")
[System.IO.File]::WriteAllText($headerPath, $headerText, [System.Text.UTF8Encoding]::new($false))
$enc = [System.Text.UTF8Encoding]::new($false, $true)
$null = $enc.GetString([System.IO.File]::ReadAllBytes($headerPath)) # UTF-8 validity gate
if (([System.IO.File]::ReadAllBytes($headerPath) -contains 13) ) { throw "header must be LF-only" }
$headerBytes = (Get-Item $headerPath).Length
$headerSha = (Get-FileHash $headerPath -Algorithm SHA256).Hash
Write-Host "[header] sha256=$headerSha bytes=$headerBytes"

# Provenance gate: refreshed later, but captured now — the tracked header
# pin in native/upstream.json must be reproduced byte-for-byte by the
# regeneration above on every host (same sources; cbindgen pinned per the
# provenance toolchain family). A divergence means the committed provenance
# and the built ABI have diverged and the M3.1a/M3.1c records must be
# requalified explicitly, never silently.
$preUpstream = Get-Content $upstreamPath -Raw | ConvertFrom-Json
$trackedHeaderSha = $preUpstream.shim.header.sha256.ToUpperInvariant()
if ($headerSha.ToUpperInvariant() -ne $trackedHeaderSha) {
    throw "regenerated header sha256 $headerSha diverges from the tracked pin $trackedHeaderSha; do not fold this into a later slice - requalify the header explicitly and record the change (M3.1a rule)."
}

# 2. Locked release build (host target; no cross builds).
Push-Location $shimDir
try {
    & $cargo test --locked
    if ($LASTEXITCODE -ne 0) { throw "cargo test --locked failed" }
    & $cargo build --release --locked
    if ($LASTEXITCODE -ne 0) { throw "cargo build --release --locked failed" }
}
finally { Pop-Location }

$dll = Join-Path (Join-Path $shimDir "target/release") $artifact
if (-not (Test-Path $dll)) { throw "shim artifact missing at $dll (host target)" }
$dllSha = (Get-FileHash $dll -Algorithm SHA256).Hash
$dllBytes = (Get-Item $dll).Length
Write-Host "[binary] sha256=$dllSha bytes=$dllBytes"

# 3. Stage binary.
if (-not (Test-Path $stagingDir)) { New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null }
Copy-Item $dll (Join-Path $stagingDir $artifact) -Force

# 4. Read provenance sources.
$upstream = Get-Content $upstreamPath -Raw | ConvertFrom-Json
$coreVersion = $upstream.minigraf.version
$coreCommit = $upstream.minigraf.commit
$identity = Get-Content (Join-Path $shimDir "identity.json") -Raw | ConvertFrom-Json
$shimAbi = $identity.shim_abi
$shimRevision = $identity.source_revision

# 5. Refresh staging manifest (per RID).
$crateSha = $upstream.shim.crate.pinned_minigraf.tarball_sha256
$crateBytes = $upstream.shim.crate.pinned_minigraf.tarball_bytes
$manifest = [ordered]@{
    rid = $Rid
    artifact = $artifact
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
    $parent = Split-Path -Parent $Path
    [System.IO.File]::WriteAllText(
        (Join-Path (Resolve-Path -LiteralPath $parent).Path (Split-Path -Leaf $Path)),
        $Text,
        [System.Text.UTF8Encoding]::new($false))
}

Write-Utf8NoBom (Join-Path $stagingDir "minigrafsharp-shim.json") ($manifest | ConvertTo-Json -Depth 5)

# 6. Refresh tracked provenance (upstream.json shim block).
# The header pin was asserted unchanged above; keep it authoritative.
if (-not $upstream.shim.assets.PSObject.Properties[$Rid]) {
    # First build for this RID: create the block with neutral values;
    # qualification flips `qualified` later, in the evidence step only.
    $asset = [pscustomobject]@{
        rid = $Rid
        binary = $artifact
        binary_sha256 = $dllSha
        binary_bytes = $dllBytes
        qualified = $false
    }
    $upstream.shim.assets | Add-Member -NotePropertyName $Rid -NotePropertyValue $asset -Force
}
$entry = $upstream.shim.assets.PSObject.Properties[$Rid].Value
$entry.rid = $Rid
$entry.binary = $artifact
$entry.binary_sha256 = $dllSha
$entry.binary_bytes = $dllBytes
# Toolchain is recorded per RID: each runner's own versions, exactly as used.
# The legacy pre-M3.1c scalar form (rustc/cargo/cbindgen strings) is migrated
# to the per-RID map here; existing per-RID entries are preserved.
$tcAll = [pscustomobject]@{}
foreach ($p in $upstream.shim.toolchain.PSObject.Properties) {
    if ($p.Name -in @("win-x64", "linux-x64", "osx-arm64")) {
        $tcAll | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value -Force
    }
}
$tcAll | Add-Member -NotePropertyName $Rid -NotePropertyValue ([pscustomobject]@{
        rustc = $rustcVersion; cargo = $cargoVersion; cbindgen = $cbindgenVersion
    }) -Force
$upstream.shim.toolchain = $tcAll
Write-Utf8NoBom $upstreamPath (($upstream | ConvertTo-Json -Depth 10) + "`n")

Write-Host "[staged] $artifact staged for $Rid; provenance refreshed (qualified flags untouched)."
