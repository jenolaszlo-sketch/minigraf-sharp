<#
.SYNOPSIS
  Verifies a built MinigrafSharp .nupkg against the pinned native metadata.

.DESCRIPTION
  Opens the package as an archive and fails on any mismatch: missing TFM,
  missing or unexpected RID native asset, native hash mismatch against
  native/upstream.json, missing license/notice/readme, wrong package
  identity, or forbidden payload (staging archives, test binaries, VCS
  data, secrets). No extraction to disk; entries are hashed in place.

.PARAMETER PackagePath
  Path to the .nupkg. Defaults to the newest package under artifacts/packages.

.PARAMETER ExpectedRids
  RIDs expected to be packaged. Defaults to win-x64 (the only RID qualified
  by execution). Update together with the library csproj and docs.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File eng/Verify-Package.ps1
#>
[CmdletBinding()]
param(
    [string]$PackagePath = "",
    [string[]]$ExpectedRids = @("win-x64")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$upstreamPath = Join-Path $repoRoot "native/upstream.json"
$upstream = Get-Content -LiteralPath $upstreamPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $pkgDir = Join-Path $repoRoot "artifacts/packages"
    $newest = Get-ChildItem -Path $pkgDir -Filter *.nupkg -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $newest) {
        throw "No .nupkg found under $pkgDir. Build one first (see docs/packaging.md)."
    }
    $PackagePath = $newest.FullName
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Package not found: $PackagePath"
}

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) {
        Write-Host "PASS: $Name"
    }
    else {
        Write-Host "FAIL: $Name"
        $failures.Add($Name)
    }
}

function Get-EntrySha256($entry) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = $entry.Open()
        try {
            $hash = $sha.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hash) -replace "-", "")
        }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $PackagePath).Path)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    Write-Host "Package: $PackagePath"
    Write-Host "Entries: $($names.Count)"

    # Managed assemblies (both TFMs).
    Check ($names -contains "lib/net8.0/MinigrafSharp.dll") "managed net8.0 assembly present"
    Check ($names -contains "lib/net10.0/MinigrafSharp.dll") "managed net10.0 assembly present"

    # Legal / readme payload.
    Check ($names -contains "LICENSE") "LICENSE present"
    Check ($names -contains "NOTICE") "NOTICE present"
    Check ($names -contains "README.md") "README present"

    # Package identity from the nuspec inside the archive.
    $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
    Check ($null -ne $nuspecEntry) "nuspec present"
    if ($null -ne $nuspecEntry) {
        $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $meta = $nuspec.package.metadata
        Check ($meta.id -eq "MinigrafSharp") "package id is MinigrafSharp"
        Check ($meta.version -like "0.1.0-preview.*") "package version is preview line ($($meta.version))"
        Check ($meta.license.type -eq "expression" -and $meta.license.'#text' -eq "Apache-2.0") "license is Apache-2.0 expression"
        Check (-not [string]::IsNullOrWhiteSpace($meta.repository.url)) "repository url present"
        $depNodes = $meta.SelectNodes(".//*[local-name()='dependency']")
        Check ($depNodes.Count -eq 0) "no external managed runtime dependencies"
    }

    # Native RID assets: present, hash-matches upstream.json, correct set.
    $packagedRids = @($names | ForEach-Object {
            if ($_ -match '^runtimes/([^/]+)/native/') { $Matches[1] }
        } | Sort-Object -Unique)
    $expectedSet = @($ExpectedRids | Sort-Object -Unique)
    Check (($packagedRids -join ",") -eq ($expectedSet -join ",")) `
        "packaged RIDs are exactly [$($expectedSet -join ', ')] (found [$($packagedRids -join ', ')])"

    foreach ($rid in $ExpectedRids) {
        $asset = $upstream.assets."$rid"
        if ($null -eq $asset) { Check $false "upstream metadata for $rid"; continue }
        $entryName = "runtimes/$rid/native/$($asset.binary)"
        Check ($names -contains $entryName) "$rid native entry present ($($asset.binary))"
        if ($names -contains $entryName) {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq $entryName } | Select-Object -First 1
            $actual = Get-EntrySha256 $entry
            $expected = $asset.binary_sha256.ToUpperInvariant()
            Check ($actual -eq $expected) "$rid native SHA-256 matches pinned metadata"
        }
    }

    # Forbidden payload.
    $forbidden = @($names | Where-Object {
            $_ -match '\.(nupkg|snupkg|zip|tar\.gz|tar\.xz|so|dylib)$' -or
            $_ -match '^\.git' -or
            $_ -match '(^|/)(MinigrafSharp\.Tests|MinigrafSharp\.IntegrationTests|MinigrafSharp\.QualificationTests|MinigrafSharp\.QuickStart|MinigrafSharp\.AotSmoke)(/|$)' -or
            $_ -match 'minigrafsharp-native\.json$' -or
            $_ -match 'staging/'
        })
    Check ($forbidden.Count -eq 0) "no forbidden payload (found: $($forbidden -join ', '))"

    # Duplicate entry names make hash checks ambiguous; reject them.
    $duplicates = @($zip.Entries | Group-Object FullName | Where-Object { $_.Count -gt 1 })
    Check ($duplicates.Count -eq 0) "no duplicate archive entries (found: $(($duplicates | ForEach-Object Name) -join ', '))"
}
finally {
    $zip.Dispose()
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "$($failures.Count) PACKAGE VERIFICATION CHECK(S) FAILED"
    exit 1
}

Write-Host ""
Write-Host "PACKAGE VERIFICATION PASSED"
exit 0
