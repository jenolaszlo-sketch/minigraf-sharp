<#
.SYNOPSIS
  Publishes and executes a trimmed consumer against the built package.

.DESCRIPTION
  Same isolation as Test-AotConsumer.ps1 (temp root outside the repository,
  local package feed, isolated NUGET_PACKAGES, MINIGRAFSHARP_NATIVE_PATH
  absent) but publishes with PublishTrimmed instead of NativeAOT. Verifies
  the trimmed app builds, deploys the native asset, executes, and produces no
  trim-analysis warnings.

.PARAMETER Rid
  Runtime identifier to publish. Defaults to the current host RID.

.PARAMETER Tfm
  Source TFM. Defaults to net10.0.

.PARAMETER KeepTemp
  Keep the temp root for debugging.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File eng/Test-TrimmedConsumer.ps1
#>
[CmdletBinding()]
param(
    [string]$Rid = "",
    [string]$Tfm = "net10.0",
    [switch]$KeepTemp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Native {
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
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { return "win-x64" }
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

if ([string]::IsNullOrWhiteSpace($Rid)) { $Rid = Get-CurrentRid }

$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$m) { Write-Host "FAIL: $m"; $failures.Add($m) }
function Pass([string]$m) { Write-Host "PASS: $m" }

$feed = Join-Path $repoRoot "artifacts/packages"
& (Join-Path $PSScriptRoot "Verify-Package.ps1") -ExpectedRids @($Rid)
if ($LASTEXITCODE -ne 0) { throw "Package verification failed." }

$pkg = Get-ChildItem -Path $feed -Filter *.nupkg | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$version = [regex]::Match($pkg.Name, '^MinigrafSharp\.(.+)\.nupkg$').Groups[1].Value
Write-Host "Trimmed smoke against MinigrafSharp $version on $Rid ($Tfm)"

$upstream = Get-Content -LiteralPath (Join-Path $repoRoot "native/upstream.json") -Raw | ConvertFrom-Json
$nativeName = $upstream.assets."$Rid".binary

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("minigrafsharp-trim-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$appDir = Join-Path $tempRoot "trim"
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
$nugetCache = Join-Path $tempRoot "nuget-packages"
New-Item -ItemType Directory -Path $nugetCache -Force | Out-Null

$repoFull = (Resolve-Path $repoRoot).Path
if ((Resolve-Path $tempRoot).Path.StartsWith($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Trim temp root must not be under the repository."
}

$program = @'
using MinigrafSharp;

string? overridePath = Environment.GetEnvironmentVariable("MINIGRAFSHARP_NATIVE_PATH");
Console.WriteLine("override=" + (string.IsNullOrEmpty(overridePath) ? "absent" : overridePath));

using var db = MinigrafDatabase.OpenMemory();
db.Execute("""(transact [[:alice :name "Alice"]])""");
var result = (MinigrafQueryResult)db.Execute("""(query [:find ?name :where [?e :name ?name]])""");
Console.WriteLine(result.Rows[0]["?name"].GetString());
'@

$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$Tfm</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishTrimmed>true</PublishTrimmed>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MinigrafSharp" Version="$version" />
  </ItemGroup>
</Project>
"@
[System.IO.File]::WriteAllText((Join-Path $appDir "trim.csproj"), $csproj)
[System.IO.File]::WriteAllText((Join-Path $appDir "Program.cs"), $program)

$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
[System.IO.File]::WriteAllText((Join-Path $tempRoot "nuget.config"), $nugetConfig)

$env:NUGET_PACKAGES = $nugetCache
$env:MINIGRAFSHARP_NATIVE_PATH = $null

try {
    $publish = Invoke-Native "dotnet" @("publish", (Join-Path $appDir "trim.csproj"), "-c", "Release", "-r", $Rid)
    $publishText = $publish.Text
    if ($publish.ExitCode -ne 0) {
        Fail "trimmed publish ($Rid/$Tfm)"
        Write-Host $publishText
        throw "Trimmed publish failed."
    }
    Pass "trimmed publish ($Rid/$Tfm)"

    $trimWarnings = [regex]::Matches($publishText, 'warning IL[0-9]{4}')
    if ($trimWarnings.Count -gt 0) {
        Fail "trim-analysis warnings present ($($trimWarnings.Count))"
        Write-Host ($publishText -split "`n" | Where-Object { $_ -match 'warning IL' })
    }
    else {
        Pass "no trim-analysis warnings"
    }

    $publishDir = Join-Path $appDir "bin/Release/$Tfm/$Rid/publish"
    $exe = Get-ChildItem -Path $publishDir -Filter "trim*" |
        Where-Object { $_.Extension -in @(".exe", "") } | Select-Object -First 1
    if ($null -eq $exe) { throw "Trimmed executable not found in $publishDir" }

    $deployed = @(
        (Join-Path $publishDir ("runtimes/$Rid/native/" + $nativeName)),
        (Join-Path $publishDir $nativeName)
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($null -eq $deployed) { Fail "native asset deployed beside trimmed output" }
    else { Pass "native asset deployed beside trimmed output ($($deployed.Replace($tempRoot, '<temp>')))" }

    $run = Invoke-Native $exe.FullName @()
    $runText = $run.Text
    if ($run.ExitCode -eq 0 -and $runText -match "override=absent" -and $runText -match "Alice") {
        Pass "trimmed execution output (override absent, Alice)"
    }
    else {
        Fail "trimmed execution output (exit $($run.ExitCode); got: $runText)"
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
    else { Write-Host "Kept temp root: $tempRoot" }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "$($failures.Count) TRIMMED CONSUMER CHECK(S) FAILED"
    exit 1
}
Write-Host ""
Write-Host "TRIMMED CONSUMER QUALIFICATION PASSED"
exit 0
