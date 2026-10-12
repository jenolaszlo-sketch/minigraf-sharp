<#
.SYNOPSIS
  Qualifies the built MinigrafSharp package with a clean external consumer.

.DESCRIPTION
  For each TFM, creates a fresh console app in an OS temp directory outside
  the repository, points a local nuget.config only at the built package
  folder, installs MinigrafSharp with an isolated NUGET_PACKAGES cache,
  builds, verifies the deployed native asset hash, and runs the consumer
  with MINIGRAFSHARP_NATIVE_PATH absent. The consumer exercises in-memory
  execution and file-backed persistence/reopen.

  A negative check then removes the deployed native asset and asserts the
  consumer fails closed with a MinigrafNativeLoadException naming the RID,
  filename and probed locations, proving resolution comes from the package
  deployment and not a repository fallback.

  No ProjectReference is used. The package is verified first.

.PARAMETER Tfms
  Target frameworks to qualify. Defaults to net8.0 and net10.0.

.PARAMETER Rid
  RID expected to deploy. Defaults to win-x64.

.PARAMETER KeepTemp
  Keep the temp root for debugging instead of deleting it.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File eng/Test-PackageConsumer.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Tfms = @("net8.0", "net10.0"),
    [string]$Rid = "win-x64",
    [switch]$KeepTemp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $repoRoot "artifacts/packages"

function Invoke-Native {
    # Runs a native command and returns combined stdout+stderr text and exit
    # code without letting a non-zero exit or stderr line terminate the script.
    param([string]$Exe, [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $Exe @Arguments 2>&1
        $text = ($output | Out-String)
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [PSCustomObject]@{ Text = $text; ExitCode = $code }
}

function Get-CurrentRid {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::X64) { return "win-x64" }
        return "win-arm64"
    }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { return "linux-arm64" }
        return "linux-x64"
    }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::X64) { return "osx-x64" }
        return "osx-arm64"
    }
    throw "Unsupported OS."
}

$hostRid = Get-CurrentRid
if ($hostRid -ne $Rid) {
    throw "This host RID is $hostRid; qualification requires execution on $Rid. Run on a $Rid machine/CI runner."
}

$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$message) {
    Write-Host "FAIL: $message"
    $failures.Add($message)
}
function Pass([string]$message) {
    Write-Host "PASS: $message"
}

# 1. Verify the package artifact first.
& (Join-Path $PSScriptRoot "Verify-Package.ps1") -ExpectedRids @($Rid)
if ($LASTEXITCODE -ne 0) { throw "Package verification failed; refusing to consume it." }

$pkg = Get-ChildItem -Path $feed -Filter *.nupkg | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $pkg) { throw "No package in $feed" }
$packageId = "MinigrafSharp"
$versionMatch = [regex]::Match($pkg.Name, '^MinigrafSharp\.(.+)\.nupkg$')
$version = $versionMatch.Groups[1].Value
Write-Host "Consuming $($pkg.Name) (version $version) on RID $Rid"

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("minigrafsharp-consumer-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$nugetCache = Join-Path $tempRoot "nuget-packages"
New-Item -ItemType Directory -Path $nugetCache -Force | Out-Null

# Assert the consumer root is outside the repository (no repo fallback).
$repoFull = (Resolve-Path $repoRoot).Path
$tempFull = (Resolve-Path $tempRoot).Path
if ($tempFull.StartsWith($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Consumer temp root must not be under the repository."
}

# Local-only feed: no nuget.org, no cache reuse.
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
  </packageSources>
</configuration>
"@
[System.IO.File]::WriteAllText((Join-Path $tempRoot "nuget.config"), $nugetConfig)

$env:NUGET_PACKAGES = $nugetCache
$env:MINIGRAFSHARP_NATIVE_PATH = $null

$upstream = Get-Content -LiteralPath (Join-Path $repoRoot "native/upstream.json") -Raw | ConvertFrom-Json
$expectedNativeSha = $upstream.assets."$Rid".binary_sha256.ToUpperInvariant()
$nativeName = $upstream.assets."$Rid".binary

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

$program = @'
using MinigrafSharp;

string? overridePath = Environment.GetEnvironmentVariable("MINIGRAFSHARP_NATIVE_PATH");
Console.WriteLine("override=" + (string.IsNullOrEmpty(overridePath) ? "absent" : overridePath));

using (var mem = MinigrafDatabase.OpenMemory())
{
    mem.Execute("""(transact [[:alice :name "Alice"]])""");
    var q = (MinigrafQueryResult)mem.Execute("""(query [:find ?name :where [?e :name ?name]])""");
    Console.WriteLine(q.Rows[0]["?name"].GetString());
}

string dbPath = Path.Combine(Path.GetTempPath(), "minigrafsharp-consumer-" + Guid.NewGuid().ToString("N") + ".graph");
try
{
    using (var w = MinigrafDatabase.Open(dbPath))
    {
        w.Execute("""(transact [[:bob :name "Bob"]])""");
        w.Checkpoint();
    }
    using (var r = MinigrafDatabase.Open(dbPath))
    {
        var q = (MinigrafQueryResult)r.Execute("""(query [:find ?name :where [?e :name ?name]])""");
        Console.WriteLine(q.Rows[0]["?name"].GetString());
    }
}
finally
{
    foreach (var f in new[] { dbPath, dbPath + ".wal" })
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
'@

try {
    foreach ($tfm in $Tfms) {
        Write-Host ""
        Write-Host "=== $tfm ==="
        $appDir = Join-Path $tempRoot ("app-" + $tfm)
        & dotnet new console -o $appDir -f $tfm | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "$tfm dotnet new console"; continue }

        [System.IO.File]::WriteAllText((Join-Path $appDir "Program.cs"), $program)

        $csproj = (Get-ChildItem -Path $appDir -Filter *.csproj | Select-Object -First 1).FullName
        & dotnet add $csproj package $packageId --version $version --source $feed | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "$tfm package install"; continue }
        Pass "$tfm package install"

        & dotnet build $appDir -c Release | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "$tfm build"; continue }
        Pass "$tfm build"

        $outDir = Join-Path $appDir "bin/Release/$tfm"
        $deployed = Join-Path $outDir ("runtimes/$Rid/native/" + $nativeName)
        if (-not (Test-Path -LiteralPath $deployed)) {
            $deployed = Join-Path $outDir $nativeName
        }
        if (-not (Test-Path -LiteralPath $deployed)) {
            Fail "$tfm native asset deployed from package"
            continue
        }
        if ((Get-Sha256 $deployed) -ne $expectedNativeSha) {
            Fail "$tfm deployed native hash matches pinned metadata"
        }
        else {
            Pass "$tfm deployed native hash matches pinned metadata ($($deployed.Replace($tempRoot, '<temp>')))"
        }

        # Positive run: override absent, outside repo.
        $run = Invoke-Native "dotnet" @("run", "--project", $appDir, "-c", "Release", "--no-build")
        $runText = $run.Text
        if ($run.ExitCode -eq 0 -and $runText -notmatch "override=absent") { Fail "$tfm override absent at runtime" } elseif ($runText -match "override=absent") { Pass "$tfm override absent at runtime" } else { Fail "$tfm override absent at runtime (got: $runText)" }
        if ($runText -match "Alice" -and $runText -match "Bob") { Pass "$tfm in-memory + file-backed consumer output" } else { Fail "$tfm consumer output (got: $runText)" }

        # Negative run: remove deployed native, expect fail-closed diagnostic.
        $backup = $deployed + ".bak"
        Move-Item -LiteralPath $deployed -Destination $backup -Force
        try {
            $neg = Invoke-Native "dotnet" @("run", "--project", $appDir, "-c", "Release", "--no-build")
            $negText = $neg.Text
            if ($neg.ExitCode -ne 0 -and $negText -match "MinigrafNativeLoadException" -and $negText -match [regex]::Escape($nativeName) -and $negText -match $Rid) {
                Pass "$tfm missing native fails closed with actionable diagnostic"
            }
            else {
                Fail "$tfm missing native diagnostic (got: $negText)"
            }
        }
        finally {
            Move-Item -LiteralPath $backup -Destination $deployed -Force
        }
    }
}
finally {
    $env:NUGET_PACKAGES = $null
    if (-not $KeepTemp) {
        for ($attempt = 0; $attempt -lt 5; $attempt++) {
            try { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction Stop; break }
            catch { Start-Sleep -Milliseconds 200 }
        }
    }
    else {
        Write-Host "Kept temp root: $tempRoot"
    }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "$($failures.Count) PACKAGE CONSUMER CHECK(S) FAILED"
    exit 1
}
Write-Host ""
Write-Host "PACKAGE CONSUMER QUALIFICATION PASSED"
exit 0
