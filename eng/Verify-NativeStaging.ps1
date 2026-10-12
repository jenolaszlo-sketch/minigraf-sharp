<#
.SYNOPSIS
  Verifies a staged Minigraf native asset for one RID (or all staged RIDs).

.DESCRIPTION
  For each staged RID under native/staging/<rid>/, checks the exact file
  inventory (the native library plus the manifest), recomputes the binary
  SHA-256 and compares it to native/upstream.json, cross-checks every manifest
  field against the pin, and confirms the binary's container format and CPU
  architecture match the RID (PE/x86-64, ELF/x86-64 or ELF/AArch64, Mach-O
  arm64 or universal). Fails closed on anything missing or mismatched.

.PARAMETER Rid
  Optional single RID. Defaults to every staged RID.

.PARAMETER StagingRoot
  Optional staging root. Defaults to native/staging.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File eng/Verify-NativeStaging.ps1 -Rid osx-arm64
#>
[CmdletBinding()]
param(
    [string]$Rid = "",
    [string]$StagingRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ($StagingRoot -eq "") { $StagingRoot = Join-Path $repoRoot "native/staging" }
$upstream = Get-Content -LiteralPath (Join-Path $repoRoot "native/upstream.json") -Raw | ConvertFrom-Json

function Fail([string]$message) { throw "STAGING-VERIFY: $message" }

$knownRids = @("win-x64", "linux-x64", "osx-arm64")
$rids = if ($Rid -ne "") { @($Rid) }
else { @(Get-ChildItem -Directory -LiteralPath $StagingRoot -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Where-Object { $_ -in $knownRids }) }
if ($rids.Count -eq 0) { Fail "no staged RIDs under $StagingRoot" }

# Reads the first bytes and confirms the container/architecture for a RID.
function Assert-Format([string]$path, [string]$rid) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 64) { Fail "$rid : $path is too small to be a native library" }
    if ($rid -eq "win-x64") {
        if (-not ($bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A)) { Fail "$rid : not a PE (MZ) binary" }
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) { Fail "$rid : missing PE signature" }
        $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
        if ($machine -ne 0x8664) { Fail ("$rid : PE machine 0x{0:X4} is not x86-64 (0x8664)" -f $machine) }
    }
    elseif ($rid -eq "linux-x64") {
        if (-not ($bytes[0] -eq 0x7F -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4C -and $bytes[3] -eq 0x46)) { Fail "$rid : not an ELF binary" }
        $machine = [BitConverter]::ToUInt16($bytes, 0x12)
        if ($machine -ne 0x3E) { Fail ("$rid : ELF machine 0x{0:X4} is not x86-64 (0x003E)" -f $machine) }
    }
    elseif ($rid -eq "osx-arm64") {
        # Universal ("fat") Mach-O stores its magic big-endian as CA FE BA BE.
        if ($bytes[0] -eq 0xCA -and $bytes[1] -eq 0xFE -and $bytes[2] -eq 0xBA -and $bytes[3] -eq 0xBE) { return }
        $magic = [BitConverter]::ToUInt32($bytes, 0)
        if ($magic -ne 0xFEEDFACF) { Fail ("$rid : magic 0x{0:X8} is neither 64-bit Mach-O nor universal" -f $magic) }
        $cpuType = [BitConverter]::ToInt32($bytes, 4)
        if ($cpuType -ne 0x0100000C) { Fail ("$rid : Mach-O cputype 0x{0:X8} is not arm64 (0x0100000C)" -f $cpuType) }
    }
    else { Fail "unknown RID '$rid'" }
}

foreach ($r in $rids) {
    if ($r -notin $knownRids) { Fail "unexpected staged directory '$r'" }
    $asset = $upstream.assets."$r"
    if ($null -eq $asset) { Fail "$r : no pin in native/upstream.json" }
    $binary = $asset.binary
    $stage = Join-Path $StagingRoot $r
    $binaryPath = Join-Path $stage $binary
    $manifestPath = Join-Path $stage "minigrafsharp-native.json"
    if (-not (Test-Path -LiteralPath $binaryPath)) { Fail "$r : missing $binary" }
    if (-not (Test-Path -LiteralPath $manifestPath)) { Fail "$r : missing minigrafsharp-native.json" }

    $inventory = @(Get-ChildItem -File -LiteralPath $stage | Select-Object -ExpandProperty Name | Sort-Object)
    $expected = @($binary, "minigrafsharp-native.json") | Sort-Object
    if (($inventory -join "|") -ne ($expected -join "|")) {
        Fail "$r : unexpected staging inventory: $($inventory -join ', ')"
    }

    $binaryHash = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($binaryHash -ne $asset.binary_sha256.ToUpperInvariant()) {
        Fail "$r : binary SHA-256 $binaryHash does not match pin $($asset.binary_sha256)"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $checks = @(
        @{ Field = "rid"; Expected = $r; Actual = $manifest.rid },
        @{ Field = "binary"; Expected = $binary; Actual = $manifest.binary },
        @{ Field = "binary_sha256"; Expected = $binaryHash; Actual = "$($manifest.binary_sha256)".ToUpperInvariant() },
        @{ Field = "release_asset"; Expected = $asset.release_asset; Actual = $manifest.release_asset },
        @{ Field = "release_sha256"; Expected = $asset.release_sha256.ToUpperInvariant(); Actual = "$($manifest.release_sha256)".ToUpperInvariant() },
        @{ Field = "minigraf_version"; Expected = $upstream.minigraf.version; Actual = $manifest.minigraf_version },
        @{ Field = "minigraf_commit"; Expected = $upstream.minigraf.commit; Actual = $manifest.minigraf_commit },
        @{ Field = "minigraf_c_version"; Expected = $upstream."minigraf-c".version; Actual = $manifest.minigraf_c_version },
        @{ Field = "minigraf_c_commit"; Expected = $upstream."minigraf-c".commit; Actual = $manifest.minigraf_c_commit },
        @{ Field = "header_sha256"; Expected = $upstream.header.sha256; Actual = "$($manifest.header_sha256)".ToUpperInvariant() }
    )
    foreach ($c in $checks) {
        if ("$($c.Actual)" -ne "$($c.Expected)") {
            Fail "$r : manifest field '$($c.Field)' is '$($c.Actual)', expected '$($c.Expected)'"
        }
    }

    Assert-Format $binaryPath $r
    Write-Output "OK $r : inventory + hash + manifest + format/arch verified ($binaryHash)"
}

Write-Output "Staging verification passed for: $($rids -join ', ')"
