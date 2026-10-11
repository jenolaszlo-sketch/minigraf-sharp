<#
.SYNOPSIS
  Fetches and stages the pinned minigraf-c native asset for one RID.

.DESCRIPTION
  Reads native/upstream.json, downloads the exact pinned release asset,
  verifies its SHA-256, extracts the native library, verifies the staged
  binary SHA-256 against the manifest, and places it at
  native/staging/<rid>/<binary> with a manifest copy.

  No floating tags: every hash comes from native/upstream.json.

.PARAMETER Rid
  .NET RID to stage (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64).
  Defaults to the current process RID.

.EXAMPLE
  pwsh eng/Get-Native.ps1
  pwsh eng/Get-Native.ps1 -Rid win-x64
#>
[CmdletBinding()]
param(
    [string]$Rid = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$upstreamPath = Join-Path $repoRoot "native/upstream.json"

function Get-CurrentRid {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { return "win-x64" }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { return "linux-arm64" }
        return "linux-x64"
    }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::X64) { return "osx-x64" }
        return "osx-arm64"
    }
    throw "Unsupported OS for native staging."
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

if ([string]::IsNullOrWhiteSpace($Rid)) {
    $Rid = Get-CurrentRid
}

$upstream = Get-Content -LiteralPath $upstreamPath -Raw | ConvertFrom-Json
$asset = $upstream.assets."$Rid"
if ($null -eq $asset) {
    throw "No pinned native asset for RID '$Rid' in native/upstream.json."
}

$url = $asset.url
$expectedReleaseSha = $asset.release_sha256.ToUpperInvariant()
$expectedBinarySha = $asset.binary_sha256.ToUpperInvariant()
$binaryName = $asset.binary

$stagingDir = Join-Path $repoRoot "native/staging/$Rid"
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

$downloadPath = Join-Path $stagingDir $asset.release_asset
if (-not (Test-Path -LiteralPath $downloadPath)) {
    Write-Host "Downloading $url ..."
    Invoke-WebRequest -Uri $url -OutFile $downloadPath
}
else {
    Write-Host "Reusing existing download: $downloadPath"
}

$actualReleaseSha = Get-Sha256 $downloadPath
if ($actualReleaseSha -ne $expectedReleaseSha) {
    throw "Release asset SHA-256 mismatch for $downloadPath. Expected $expectedReleaseSha, got $actualReleaseSha."
}
Write-Host "Release asset hash verified: $actualReleaseSha"

$extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("minigraf-native-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
try {
    if ($downloadPath.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -LiteralPath $downloadPath -DestinationPath $extractDir -Force
    }
    else {
        tar -xzf $downloadPath -C $extractDir
        if ($LASTEXITCODE -ne 0) { throw "tar extraction failed for $downloadPath." }
    }

    $candidate = Get-ChildItem -Path $extractDir -Recurse -File -Filter $binaryName | Select-Object -First 1
    if ($null -eq $candidate) {
        throw "Native binary '$binaryName' not found inside $downloadPath."
    }

    $stagedPath = Join-Path $stagingDir $binaryName
    Copy-Item -LiteralPath $candidate.FullName -Destination $stagedPath -Force
}
finally {
    Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
}

$actualBinarySha = Get-Sha256 $stagedPath
if ($actualBinarySha -ne $expectedBinarySha) {
    throw "Staged binary SHA-256 mismatch for $stagedPath. Expected $expectedBinarySha, got $actualBinarySha."
}

$stagingManifest = ([ordered]@{
    rid = $Rid
    binary = $binaryName
    binary_sha256 = $actualBinarySha
    release_asset = $asset.release_asset
    release_sha256 = $actualReleaseSha
    minigraf_version = $upstream.minigraf.version
    minigraf_commit = $upstream.minigraf.commit
    minigraf_c_version = $upstream."minigraf-c".version
    minigraf_c_commit = $upstream."minigraf-c".commit
    header_sha256 = $upstream.header.sha256
} | ConvertTo-Json)
[System.IO.File]::WriteAllText(
    (Join-Path $stagingDir "minigrafsharp-native.json"),
    $stagingManifest,
    (New-Object System.Text.UTF8Encoding $false))

Write-Host "Staged $stagedPath ($actualBinarySha)"
Write-Host "Manifest: $(Join-Path $stagingDir 'minigrafsharp-native.json')"
