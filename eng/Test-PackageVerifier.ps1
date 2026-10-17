<#
.SYNOPSIS
  Self-tests the package verifier against a matrix of tampered packages.

.DESCRIPTION
  Builds controlled tampered variants of the built package (with forward-slash
  archive entries) and asserts eng/Verify-Package.ps1 rejects each one, plus a
  control asserting the untampered package passes. This makes the verifier's
  fail-closed behavior deterministic rather than manually tested once.

.PARAMETER PackagePath
  Base package. Defaults to the newest under artifacts/packages.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File eng/Test-PackageVerifier.ps1
#>
[CmdletBinding()]
param([string]$PackagePath = "")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$verifyScript = Join-Path $PSScriptRoot "Verify-Package.ps1"

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $newest = Get-ChildItem -Path (Join-Path $repoRoot "artifacts/packages") -Filter *.nupkg -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $newest) { throw "No package found to self-test." }
    $PackagePath = $newest.FullName
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$upstream = Get-Content -LiteralPath (Join-Path $repoRoot "native/upstream.json") -Raw | ConvertFrom-Json
# Shim artifact names follow the loader convention (the `shim.assets` record
# is optional provenance).
function Get-ShimBinaryName([string]$rid) {
    if ($rid.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) { return "minigrafsharp_shim.dll" }
    if ($rid.StartsWith("osx-", [StringComparison]::OrdinalIgnoreCase)) { return "libminigrafsharp_shim.dylib" }
    return "libminigrafsharp_shim.so"
}
$nativeName = $upstream.assets."win-x64".binary
$nativeEntry = "runtimes/win-x64/native/$nativeName"
$shimName = Get-ShimBinaryName "win-x64"
$shimEntry = "runtimes/win-x64/native/$shimName"

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Host "PASS: $Name" }
    else { Write-Host "FAIL: $Name"; $failures.Add($Name) }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("minigrafsharp-verifier-selftest-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

function Write-ZipFromDir([string]$dir, [string]$dest, [switch]$DuplicateNative) {
    $zip = [System.IO.Compression.ZipFile]::Open($dest, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -Recurse -File -LiteralPath $dir) {
            $rel = $file.FullName.Substring($dir.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry($rel)
            $stream = $entry.Open()
            $source = [System.IO.File]::OpenRead($file.FullName)
            try { $source.CopyTo($stream) } finally { $source.Dispose(); $stream.Dispose() }
        }

        if ($DuplicateNative) {
            $source = [System.IO.File]::OpenRead((Join-Path $dir $nativeEntry))
            $entry = $zip.CreateEntry($nativeEntry)
            $stream = $entry.Open()
            try { $source.CopyTo($stream) } finally { $source.Dispose(); $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function New-Tampered([scriptblock]$Mutate, [switch]$DuplicateNative) {
    $extract = Join-Path $tempRoot ([System.Guid]::NewGuid().ToString("N"))
    [System.IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $extract)
    & $Mutate $extract
    $dest = Join-Path $tempRoot ([System.Guid]::NewGuid().ToString("N") + ".nupkg")
    Write-ZipFromDir $extract $dest -DuplicateNative:$DuplicateNative
    Remove-Item -Recurse -Force $extract
    return $dest
}

# Run the verifier in a child PowerShell so its `exit` code is captured without
# terminating this self-test. Use the current host executable so it works with
# both Windows PowerShell (powershell.exe) and PowerShell 7 (pwsh).
$psExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName

function Test-Rejected([string]$name, [string]$path) {
    & $psExe -NoProfile -ExecutionPolicy Bypass -File $verifyScript -PackagePath $path *> $null
    Check ($LASTEXITCODE -ne 0) "tamper rejected: $name"
}

try {
    # Control: the untampered package must pass.
    & $psExe -NoProfile -ExecutionPolicy Bypass -File $verifyScript -PackagePath $PackagePath *> $null
    Check ($LASTEXITCODE -eq 0) "control: untampered package passes verification"

    Test-Rejected "remove native dll" (New-Tampered { param($d) Remove-Item -LiteralPath (Join-Path $d $nativeEntry) })
    Test-Rejected "modify native dll byte" (New-Tampered {
            param($d)
            $p = Join-Path $d $nativeEntry
            $bytes = [System.IO.File]::ReadAllBytes($p)
            $bytes[1000] = $bytes[1000] -bxor 0xFF
            [System.IO.File]::WriteAllBytes($p, $bytes)
        })
    Test-Rejected "replace native dll with garbage" (New-Tampered {
            param($d)
            [System.IO.File]::WriteAllBytes((Join-Path $d $nativeEntry), (New-Object byte[] 2048))
        })
    Test-Rejected "rename native entry path" (New-Tampered {
            param($d)
            Move-Item -LiteralPath (Join-Path $d $nativeEntry) -Destination (Join-Path $d "runtimes/win-x64/native/renamed.dll")
        })
    Test-Rejected "remove shim dll" (New-Tampered { param($d) Remove-Item -LiteralPath (Join-Path $d $shimEntry) })
    Test-Rejected "modify shim dll byte" (New-Tampered {
            param($d)
            $p = Join-Path $d $shimEntry
            $bytes = [System.IO.File]::ReadAllBytes($p)
            $bytes[1000] = $bytes[1000] -bxor 0xFF
            [System.IO.File]::WriteAllBytes($p, $bytes)
        })
    Test-Rejected "replace shim dll with engine binary" (New-Tampered {
            param($d)
            Copy-Item -LiteralPath (Join-Path $d $nativeEntry) -Destination (Join-Path $d $shimEntry) -Force
        })
    Test-Rejected "remove managed net8 dll" (New-Tampered { param($d) Remove-Item -LiteralPath (Join-Path $d "lib/net8.0/MinigrafSharp.dll") })
    Test-Rejected "remove managed net10 dll" (New-Tampered { param($d) Remove-Item -LiteralPath (Join-Path $d "lib/net10.0/MinigrafSharp.dll") })
    Test-Rejected "remove LICENSE" (New-Tampered { param($d) Remove-Item -LiteralPath (Join-Path $d "LICENSE") })
    Test-Rejected "remove NOTICE" (New-Tampered { param($d) Remove-Item -LiteralPath (Join-Path $d "NOTICE") })
    Test-Rejected "add unexpected RID asset" (New-Tampered {
            param($d)
            $dir = Join-Path $d "runtimes/linux-x64/native"
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            [System.IO.File]::WriteAllBytes((Join-Path $dir "libminigraf.so"), (New-Object byte[] 16))
        })
    Test-Rejected "duplicate native entry" (New-Tampered -DuplicateNative {} )
}
finally {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction Stop; break }
        catch { Start-Sleep -Milliseconds 200 }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "$($failures.Count) PACKAGE VERIFIER SELF-TEST CHECK(S) FAILED"
    exit 1
}
Write-Host ""
Write-Host "PACKAGE VERIFIER SELF-TEST PASSED"
exit 0
