<#
.SYNOPSIS
  Publishes and executes the NativeAOT sample against the built package.

.DESCRIPTION
  Copies samples/MinigrafSharp.AotSmoke to an OS temp directory outside the
  repository, points NuGet at the local package feed (plus nuget.org for the
  ILCompiler toolchain), installs the generated MinigrafSharp package with an
  isolated NUGET_PACKAGES cache, publishes NativeAOT, verifies the native
  asset deployment, and runs the produced executable with
  MINIGRAFSHARP_NATIVE_PATH absent.

  Running outside the repository matters: it prevents the loader's
  repository staging walk-up from masking a package deployment problem.

.PARAMETER Rid
  Runtime identifier to publish. Defaults to the current host RID.

.PARAMETER Tfm
  Source TFM of the sample. Defaults to net10.0.

.PARAMETER KeepTemp
  Keep the temp root for debugging.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File eng/Test-AotConsumer.ps1
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
Write-Host "AOT smoke against MinigrafSharp $version on $Rid ($Tfm)"

$upstream = Get-Content -LiteralPath (Join-Path $repoRoot "native/upstream.json") -Raw | ConvertFrom-Json
$nativeName = $upstream.assets."$Rid".binary

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("minigrafsharp-aot-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$appDir = Join-Path $tempRoot "aot"
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
$nugetCache = Join-Path $tempRoot "nuget-packages"
New-Item -ItemType Directory -Path $nugetCache -Force | Out-Null

$repoFull = (Resolve-Path $repoRoot).Path
if ((Resolve-Path $tempRoot).Path.StartsWith($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "AOT temp root must not be under the repository."
}

Copy-Item (Join-Path $repoRoot "samples/MinigrafSharp.AotSmoke/MinigrafSharp.AotSmoke.csproj") $appDir
Copy-Item (Join-Path $repoRoot "samples/MinigrafSharp.AotSmoke/Program.cs") $appDir
$csprojPath = Join-Path $appDir "MinigrafSharp.AotSmoke.csproj"
$csprojText = [System.IO.File]::ReadAllText($csprojPath)
$csprojText = $csprojText -replace 'Version="[^"]*"', "Version=`"$version`""
[System.IO.File]::WriteAllText($csprojPath, $csprojText)

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
    $publish = Invoke-Native "dotnet" @("publish", $csprojPath, "-c", "Release", "-r", $Rid)
    $publishText = $publish.Text

    if ($publish.ExitCode -ne 0) {
        Fail "NativeAOT publish ($Rid/$Tfm)"
        Write-Host $publishText
        throw "NativeAOT publish failed."
    }
    Pass "NativeAOT publish ($Rid/$Tfm)"

    # Treat trim/AOT analysis warnings as findings, not noise.
    $analysisWarnings = [regex]::Matches($publishText, 'warning (IL[0-9]{4}|ILC[0-9]{4})')
    if ($analysisWarnings.Count -gt 0) {
        Fail "NativeAOT/trim analysis warnings present ($($analysisWarnings.Count))"
        Write-Host ($publishText -split "`n" | Where-Object { $_ -match 'warning IL' })
    }
    else {
        Pass "no NativeAOT/trim analysis warnings"
    }

    $publishDir = Join-Path $appDir "bin/Release/$Tfm/$Rid/publish"
    $exe = Get-ChildItem -Path $publishDir -Filter "MinigrafSharp.AotSmoke*" |
        Where-Object { $_.Extension -in @(".exe", "") } | Select-Object -First 1
    if ($null -eq $exe) { throw "Published executable not found in $publishDir" }

    # Native asset must be deployed alongside the AOT output.
    $deployedCandidates = @(
        (Join-Path $publishDir ("runtimes/$Rid/native/" + $nativeName)),
        (Join-Path $publishDir $nativeName)
    )
    $deployed = $deployedCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($null -eq $deployed) {
        Fail "native asset deployed beside AOT output ($nativeName)"
    }
    else {
        Pass "native asset deployed beside AOT output ($($deployed.Replace($tempRoot, '<temp>')))"
    }

    $run = Invoke-Native $exe.FullName @()
    $runText = $run.Text
    if ($run.ExitCode -eq 0 -and $runText -match "override=absent" -and $runText -match "Alice") {
        Pass "AOT execution output (override absent, Alice)"
    }
    else {
        Fail "AOT execution output (exit $($run.ExitCode); got: $runText)"
    }

    # Negative: remove the deployed native from a copy; must fail closed, not crash.
    $negDir = Join-Path $tempRoot "aot-negative"
    Copy-Item -Path $publishDir -Destination $negDir -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $negDir $nativeName) -Force
    $negExe = Get-ChildItem -Path $negDir -Filter "MinigrafSharp.AotSmoke*" |
        Where-Object { $_.Extension -in @(".exe", "") } | Select-Object -First 1
    if ($null -eq $negExe) {
        Fail "AOT negative copy executable present"
    }
    else {
        $negRun = Invoke-Native $negExe.FullName @()
        if ($negRun.ExitCode -ne 0 -and $negRun.Text -match "MinigrafNativeLoadException") {
            Pass "AOT missing native fails closed"
        }
        else {
            Fail "AOT missing native (exit $($negRun.ExitCode); got: $($negRun.Text))"
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
    else { Write-Host "Kept temp root: $tempRoot" }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "$($failures.Count) AOT CONSUMER CHECK(S) FAILED"
    exit 1
}
Write-Host ""
Write-Host "AOT CONSUMER QUALIFICATION PASSED"
exit 0
