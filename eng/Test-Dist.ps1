<#
.SYNOPSIS
  Runs one RID's distribution qualification gate on its own OS.

.DESCRIPTION
  1. eng/Get-Native.ps1 -Rid <rid> stages and hash-verifies the pinned asset.
  2. Builds the solution and runs the real-engine unit, integration and
     storage/recovery qualification suites for net8.0 and net10.0 on this OS.
  3. eng/Build-Shim.ps1 -Rid <rid> builds the owned shim for this host
     (cargo --locked unit tests + release build), stages it next to the
     engine, and refreshes the shim provenance pins.
  4. eng/Verify-NativeStaging.ps1 -Rid <rid> verifies the complete staged
     inventory (official engine + shim), hashes, manifests and formats.
  5. Runs the shim harness and the managed prepared-query qualification
     harness for net8.0 and net10.0.

  The caller uploads native/staging/<rid> as the CI artifact consumed by the
  pack job. See .github/workflows/ci.yml and docs/packaging.md.

.EXAMPLE
  pwsh -NoProfile -File eng/Test-Dist.ps1 -Rid linux-x64
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "linux-x64", "osx-arm64")]
    [string]$Rid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail([string]$message) { throw "DIST($Rid): $message" }
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $Exe @Arguments 2>&1
        return [PSCustomObject]@{ Text = ($output | Out-String); ExitCode = $LASTEXITCODE }
    }
    finally { $ErrorActionPreference = $previous }
}

$repoRoot = Split-Path -Parent $PSScriptRoot

function Run([string]$label, [string[]]$dotnetArgs) {
    $result = Invoke-Native "dotnet" $dotnetArgs
    if ($result.ExitCode -ne 0) { Fail "$label failed ($($result.ExitCode)) :: $($result.Text)" }
    Write-Output "OK $label"
}

& (Join-Path $PSScriptRoot "Get-Native.ps1") -Rid $Rid

Push-Location -LiteralPath $repoRoot
try {
    Run "restore" @("restore", "MinigrafSharp.slnx")
    Run "build" @("build", "MinigrafSharp.slnx", "-c", "Release", "--no-restore")

    foreach ($tfm in @("net8.0", "net10.0")) {
        Run "unit $tfm" @("run", "--project", "tests/MinigrafSharp.Tests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "integration $tfm" @("run", "--project", "tests/MinigrafSharp.IntegrationTests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "qualification $tfm" @("run", "--project", "tests/MinigrafSharp.QualificationTests", "-c", "Release", "--no-build", "-f", $tfm)
    }
}
finally { Pop-Location }

& (Join-Path $PSScriptRoot "Build-Shim.ps1") -Rid $Rid
& (Join-Path $PSScriptRoot "Verify-NativeStaging.ps1") -Rid $Rid

Push-Location -LiteralPath $repoRoot
try {
    Run "restore (shim harnesses)" @("restore", "MinigrafSharp.slnx")
    Run "build (shim harnesses)" @("build", "MinigrafSharp.slnx", "-c", "Release", "--no-restore")

    foreach ($tfm in @("net8.0", "net10.0")) {
        Run "shim $tfm" @("run", "--project", "tests/MinigrafSharp.ShimTests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "prepared $tfm" @("run", "--project", "tests/MinigrafSharp.PreparedQualificationTests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "transactions $tfm" @("run", "--project", "tests/MinigrafSharp.TransactionQualificationTests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "options $tfm" @("run", "--project", "tests/MinigrafSharp.OpenOptionsQualificationTests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "errors $tfm" @("run", "--project", "tests/MinigrafSharp.StructuredErrorQualificationTests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "values $tfm" @("run", "--project", "tests/MinigrafSharp.TypedValueQualificationTests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "identity $tfm" @("run", "--project", "tests/MinigrafSharp.RuntimeIdentityQualificationTests", "-c", "Release", "--no-build", "-f", $tfm)
        Run "integrated $tfm" @("run", "--project", "tests/MinigrafSharp.IntegratedQualificationTests", "-c", "Release", "--no-build", "-f", $tfm)
    }
}
finally { Pop-Location }

Write-Output "Distribution gate passed ($Rid)."
exit 0
