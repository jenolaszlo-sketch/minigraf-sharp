<#
.SYNOPSIS
  Clean external package consumer against a built nupkg (distribution gate).

.DESCRIPTION
  Creates an isolated console project outside the repository with an isolated
  NuGet cache, restores the selected MinigrafSharp package, and exercises
  in-memory execution (typed + raw), Unicode, and file-backed
  write/checkpoint/reopen. Asserts exit code 0 and the expected stdout markers.
  Also runs loader hardening on the built consumer: PATH/cwd contamination,
  missing-native fail-closed, and the MINIGRAFSHARP_NATIVE_PATH override
  matrix (valid / missing / corrupt / wrong-architecture, where the
  wrong-architecture asset is another RID's library taken from the package).

  With -Aot it publishes and executes the consumer as NativeAOT for -Rid; with
  -Trim it publishes and executes it trimmed for -Rid. With -UsePublicFeed it
  installs from nuget.org (for published-package verification) and additionally
  compares the restored archive to the selected artifact.

.PARAMETER Package
  Path to the nupkg under test.

.PARAMETER TargetFramework
  net8.0 or net10.0.

.PARAMETER Rid
  RID for AOT/trimmed publish (required with -Aot or -Trim).

.PARAMETER Aot
  Publish and run the consumer as NativeAOT.

.PARAMETER Trim
  Publish and run the consumer trimmed.

.PARAMETER UsePublicFeed
  Install from nuget.org instead of a local folder feed.

.PARAMETER SkipHardening
  Skip the loader hardening checks (used when AOT/Trim already covers loading).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File eng/Test-PackageConsumer.ps1 -Package artifacts/packages/MinigrafSharp.0.1.0-preview.5.nupkg -TargetFramework net10.0 -Aot -Rid win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [ValidateSet("net8.0", "net10.0")][string]$TargetFramework = "net10.0",
    [ValidateSet("win-x64", "linux-x64", "osx-arm64")][string]$Rid,
    [switch]$Aot,
    [switch]$Trim,
    [switch]$UsePublicFeed,
    [switch]$SkipHardening
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail([string]$message) { throw "PACKAGED-CONSUMER: $message" }
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

function Get-HostRid {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { return "win-x64" }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) { return "linux-x64" }
    return "osx-arm64"
}

# Shim artifact names follow the loader convention; the `shim.assets` record
# is optional provenance, so the name must not depend on its presence.
function Get-ShimBinaryName([string]$rid) {
    if ($rid.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) { return "minigrafsharp_shim.dll" }
    if ($rid.StartsWith("osx-", [StringComparison]::OrdinalIgnoreCase)) { return "libminigrafsharp_shim.dylib" }
    return "libminigrafsharp_shim.so"
}

function Get-ArchiveHashes([string]$archivePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $map = @{}
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith("/") -or $entry.FullName -eq ".signature.p7s") { continue }
            if ($map.ContainsKey($entry.FullName)) { Fail "duplicate archive entry '$($entry.FullName)'" }
            $stream = $entry.Open()
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try { $map[$entry.FullName] = [System.BitConverter]::ToString($sha.ComputeHash($stream)) }
            finally { $sha.Dispose(); $stream.Dispose() }
        }
        return $map
    }
    finally { $archive.Dispose() }
}

function Get-PackageEntrySha256([string]$archivePath, [string]$entryName) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $entryName } | Select-Object -First 1
        if ($null -eq $entry) { Fail "package is missing $entryName" }
        $stream = $entry.Open()
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "") }
        finally { $sha.Dispose(); $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

if (($Aot -or $Trim) -and -not $Rid) { Fail "-Rid is required with -Aot or -Trim" }
if (-not $Aot -and -not $Trim -and $Rid) { Fail "-Rid is only used with -Aot or -Trim" }
if (($Aot -or $Trim) -and $UsePublicFeed) { Fail "-Aot/-Trim consume the selected local candidate; omit -UsePublicFeed" }

if (-not (Test-Path -LiteralPath $Package)) { Fail "package not found: $Package" }
$Package = (Resolve-Path -LiteralPath $Package).Path
$version = [System.IO.Path]::GetFileNameWithoutExtension($Package) -replace '^MinigrafSharp\.', ''
if ($version -eq [System.IO.Path]::GetFileNameWithoutExtension($Package)) { Fail "cannot parse version from $Package" }

$repoRoot = Split-Path -Parent $PSScriptRoot
$upstream = Get-Content -LiteralPath (Join-Path $repoRoot "native/upstream.json") -Raw | ConvertFrom-Json
$hostRid = Get-HostRid
$nativeName = $upstream.assets."$hostRid".binary
$expectedNativeSha = $upstream.assets."$hostRid".binary_sha256.ToUpperInvariant()
$shimName = Get-ShimBinaryName $hostRid
# The shim is built per RID and is not byte-reproducible across environments;
# chain of custody is package entry -> deployment. The engine is an upstream
# release artifact and keeps its pinned hash.

$previousPackages = $env:NUGET_PACKAGES
$previousNativePath = $env:MINIGRAFSHARP_NATIVE_PATH
$previousShimPath = $env:MINIGRAFSHARP_SHIM_NATIVE_PATH
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("minigrafsharp-consumer-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null

$markers = @("override=absent", "Alice", "raw=True", "uni=True", "prepared=True", "Bob")

$program = @'
using MinigrafSharp;

string? ov = Environment.GetEnvironmentVariable("MINIGRAFSHARP_NATIVE_PATH");
Console.WriteLine("override=" + (string.IsNullOrEmpty(ov) ? "absent" : ov));

using (var mem = MinigrafDatabase.OpenMemory())
{
    mem.Execute("""(transact [[:alice :name "Alice"]])""");
    var typed = (MinigrafQueryResult)mem.Execute("""(query [:find ?name :where [?e :name ?name]])""");
    Console.WriteLine(typed.Rows[0]["?name"].GetString());
    string raw = mem.ExecuteRaw("""(query [:find ?name :where [?e :name ?name]])""");
    Console.WriteLine("raw=" + raw.Contains("Alice"));
}

const string Uni = "Z\u00fcrich \u2744 \u65e5\u672c\u8a9e \U0001F642";
using (var um = MinigrafDatabase.OpenMemory())
{
    um.Execute($"""(transact [[:u :v "{Uni}"]])""");
    var uq = (MinigrafQueryResult)um.Execute("(query [:find ?v :where [:u :v ?v]])");
    Console.WriteLine("uni=" + (uq.Rows[0]["?v"].GetString() == Uni));
}

// Prepared-query path over the owned shim, loaded through the same
// package-deployed runtimes/<rid>/native/ layout as the engine.
using (var pd = MinigrafPreparedDatabase.OpenMemory())
{
    pd.Execute("""(transact [[:alice :name "Alice"]])""");
    var pq = pd.Prepare("""(query [:find ?name :where [?e :name ?name]])""");
    var pqr = (MinigrafQueryResult)pq.Execute(new Dictionary<string, MinigrafBindValue>());
    Console.WriteLine("prepared=" + (pqr.Rows[0]["?name"].GetString() == "Alice"));
}

string db = Path.Combine(Path.GetTempPath(), "minigrafsharp-consumer-" + Guid.NewGuid().ToString("N") + ".graph");
try
{
    using (var w = MinigrafDatabase.Open(db))
    {
        w.Execute("""(transact [[:bob :name "Bob"]])""");
        w.Checkpoint();
    }
    using (var r = MinigrafDatabase.Open(db))
    {
        var q = (MinigrafQueryResult)r.Execute("""(query [:find ?name :where [?e :name ?name]])""");
        Console.WriteLine(q.Rows[0]["?name"].GetString());
    }
}
finally
{
    foreach (var f in new[] { db, db + ".wal" })
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
'@

$csprojText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$TargetFramework</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MinigrafSharp" Version="$version" />
  </ItemGroup>
</Project>
"@

try {
    Remove-Item Env:\MINIGRAFSHARP_NATIVE_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\MINIGRAFSHARP_SHIM_NATIVE_PATH -ErrorAction SilentlyContinue

    $csproj = Join-Path $work "Consumer.csproj"
    [System.IO.File]::WriteAllText($csproj, $csprojText)
    [System.IO.File]::WriteAllText((Join-Path $work "Program.cs"), $program)

    $feed = Join-Path $work "local-packages"
    New-Item -ItemType Directory -Path $feed | Out-Null
    Copy-Item -LiteralPath $Package -Destination $feed -Force

    if ($UsePublicFeed) {
        $sources = '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
        $mapping = ""
    }
    else {
        $sources = @'
    <add key="local" value="./local-packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
'@
        $mapping = @'
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="MinigrafSharp" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
'@
    }
    [System.IO.File]::WriteAllText((Join-Path $work "nuget.config"), @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
$sources
  </packageSources>
$mapping
</configuration>
"@)

    $env:NUGET_PACKAGES = Join-Path $work "nuget-cache"
    $add = Invoke-Native "dotnet" @("add", $csproj, "package", "MinigrafSharp", "--version", $version)
    if ($add.ExitCode -ne 0) { Fail "package restore failed (isolated cache) :: $($add.Text)" }

    $cached = Join-Path $env:NUGET_PACKAGES "minigrafsharp/$version/minigrafsharp.$version.nupkg"
    if (-not (Test-Path -LiteralPath $cached)) { Fail "restored archive missing: $cached" }
    $expectedHashes = Get-ArchiveHashes $Package
    $actualHashes = Get-ArchiveHashes $cached
    if ($actualHashes.Count -ne $expectedHashes.Count) { Fail "restored archive entry count differs from selected artifact" }
    foreach ($name in $expectedHashes.Keys) {
        if ($actualHashes[$name] -ne $expectedHashes[$name]) { Fail "restored package content differs from selected artifact: $name" }
    }
    Write-Output "Restored MinigrafSharp archive matches the selected candidate."

    Push-Location -LiteralPath $work
    try {
        $run = Invoke-Native "dotnet" @("run", "--configuration", "Release", "--framework", $TargetFramework)
        if ($run.ExitCode -ne 0) { Fail "consumer exited $($run.ExitCode) :: $($run.Text)" }
        foreach ($marker in $markers) {
            if ($run.Text -notmatch [regex]::Escape($marker)) { Fail "missing stdout marker '$marker' :: $($run.Text)" }
        }
        Write-Output "OK consumer ($TargetFramework): all markers present"
    }
    finally { Pop-Location }

    $outDir = Join-Path $work "bin/Release/$TargetFramework"
    $expectedShimSha = Get-PackageEntrySha256 $Package "runtimes/$hostRid/native/$shimName"
    foreach ($deployedTarget in @(
            @{ Name = $nativeName; Sha = $expectedNativeSha; Label = "engine" },
            @{ Name = $shimName; Sha = $expectedShimSha; Label = "shim" })) {
        $deployed = Join-Path $outDir ("runtimes/$hostRid/native/" + $deployedTarget.Name)
        if (-not (Test-Path -LiteralPath $deployed)) { $deployed = Join-Path $outDir $deployedTarget.Name }
        if (-not (Test-Path -LiteralPath $deployed)) { Fail "$($deployedTarget.Label) native asset not deployed from package" }
        if ((Get-FileHash -LiteralPath $deployed -Algorithm SHA256).Hash.ToUpperInvariant() -ne $deployedTarget.Sha) {
            Fail "deployed $($deployedTarget.Label) SHA-256 does not match pinned metadata"
        }
    }
    Write-Output "Deployed native assets verified (engine vs pinned metadata, shim vs packaged entry: $nativeName, $shimName)."

    if (-not $SkipHardening -and -not $Aot -and -not $Trim) {
        $exe = (Get-ChildItem -Path $outDir -Filter "Consumer*" | Where-Object { $_.Extension -in @(".exe", "") } | Select-Object -First 1).FullName
        if (-not $exe) { Fail "consumer executable not found for hardening" }
        $hardeningWork = Join-Path $work "hardening"
        New-Item -ItemType Directory -Path $hardeningWork | Out-Null

        # PATH contamination: a rogue same-named library on PATH must be ignored.
        $rogue = Join-Path $work "rogue-path"
        New-Item -ItemType Directory -Path $rogue | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $rogue $nativeName), (New-Object byte[] 4096))
        $savedPath = $env:PATH
        $env:PATH = $rogue + [System.IO.Path]::PathSeparator + $savedPath
        try { $pathRun = Invoke-Native $exe @() } finally { $env:PATH = $savedPath }
        if ($pathRun.ExitCode -ne 0 -or $pathRun.Text -notmatch "Alice") { Fail "PATH rogue hijack check :: $($pathRun.Text)" }
        Write-Output "OK PATH rogue ignored."

        # cwd contamination: a rogue same-named library in cwd must be ignored.
        $rogueCwd = Join-Path $work "rogue-cwd"
        New-Item -ItemType Directory -Path $rogueCwd | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $rogueCwd $nativeName), (New-Object byte[] 4096))
        $prevLoc = Get-Location
        try { Set-Location $rogueCwd; $cwdRun = Invoke-Native $exe @() } finally { Set-Location $prevLoc }
        if ($cwdRun.ExitCode -ne 0 -or $cwdRun.Text -notmatch "Alice") { Fail "cwd rogue hijack check :: $($cwdRun.Text)" }
        Write-Output "OK cwd rogue ignored."

        # Missing native: remove the deployed asset, expect fail-closed.
        $backup = $deployed + ".bak"
        Move-Item -LiteralPath $deployed -Destination $backup -Force
        try { $missingRun = Invoke-Native $exe @() } finally { Move-Item -LiteralPath $backup -Destination $deployed -Force }
        if ($missingRun.ExitCode -eq 0 -or $missingRun.Text -notmatch "MinigrafNativeLoadException") { Fail "missing native did not fail closed :: $($missingRun.Text)" }
        Write-Output "OK missing native fails closed."

        # Override matrix.
        $validRun = Invoke-Native $exe @()
        if ($validRun.ExitCode -ne 0) { Fail "override baseline run failed" }

        $prevOverride = $env:MINIGRAFSHARP_NATIVE_PATH
        try {
            $env:MINIGRAFSHARP_NATIVE_PATH = Join-Path $work "nope.dll"
            $ovMissing = Invoke-Native $exe @()
            if ($ovMissing.ExitCode -eq 0 -or $ovMissing.Text -notmatch "MinigrafNativeLoadException") { Fail "override missing did not fail closed" }
            Write-Output "OK override missing fails closed (no fallthrough)."

            $corrupt = Join-Path $work "corrupt.dll"
            [System.IO.File]::WriteAllBytes($corrupt, (New-Object byte[] 4096))
            $env:MINIGRAFSHARP_NATIVE_PATH = $corrupt
            $ovCorrupt = Invoke-Native $exe @()
            if ($ovCorrupt.ExitCode -eq 0 -or $ovCorrupt.Text -notmatch "MinigrafNativeLoadException") { Fail "override corrupt did not fail closed" }
            Write-Output "OK override corrupt fails closed."

            # Wrong architecture: another RID's library extracted from the package.
            $wrongRid = @("win-x64", "linux-x64", "osx-arm64") | Where-Object { $_ -ne $hostRid } | Select-Object -First 1
            $wrongName = $upstream.assets."$wrongRid".binary
            $wrongExtract = Join-Path $work "wrong-arch"
            New-Item -ItemType Directory -Path $wrongExtract | Out-Null
            $wrongPath = Join-Path $wrongExtract ("wrong-" + $wrongName)
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [System.IO.Compression.ZipFile]::OpenRead($Package)
            try {
                $entryName = "runtimes/$wrongRid/native/$wrongName"
                $entry = $zip.Entries | Where-Object { $_.FullName -eq $entryName } | Select-Object -First 1
                if ($null -eq $entry) { Fail "package is missing $entryName for wrong-arch probe" }
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $wrongPath, $true)
            }
            finally { $zip.Dispose() }
            $env:MINIGRAFSHARP_NATIVE_PATH = $wrongPath
            $ovWrong = Invoke-Native $exe @()
            if ($ovWrong.ExitCode -eq 0 -or $ovWrong.Text -notmatch "MinigrafNativeLoadException") { Fail "override wrong-architecture did not fail closed" }
            Write-Output "OK override wrong-architecture ($wrongRid) fails closed."
        }
        finally {
            if ($null -eq $prevOverride) { Remove-Item Env:\MINIGRAFSHARP_NATIVE_PATH -ErrorAction SilentlyContinue }
            else { $env:MINIGRAFSHARP_NATIVE_PATH = $prevOverride }
        }

        # Shim override matrix (MINIGRAFSHARP_SHIM_NATIVE_PATH): the prepared
        # path must fail closed with no fallback in every corruption mode.
        $prevShimOverride = $env:MINIGRAFSHARP_SHIM_NATIVE_PATH
        try {
            $env:MINIGRAFSHARP_SHIM_NATIVE_PATH = Join-Path $work "nope-shim.abi"
            $shMissing = Invoke-Native $exe @()
            if ($shMissing.ExitCode -eq 0 -or $shMissing.Text -notmatch "MinigrafNativeLoadException") { Fail "shim override missing did not fail closed :: $($shMissing.Text)" }
            Write-Output "OK shim override missing fails closed (no fallthrough)."

            $corruptShimFile = Join-Path $work "corrupt-shim.bin"
            [System.IO.File]::WriteAllBytes($corruptShimFile, (New-Object byte[] 4096))
            $env:MINIGRAFSHARP_SHIM_NATIVE_PATH = $corruptShimFile
            $shCorrupt = Invoke-Native $exe @()
            if ($shCorrupt.ExitCode -eq 0 -or $shCorrupt.Text -notmatch "MinigrafNativeLoadException") { Fail "shim override corrupt did not fail closed :: $($shCorrupt.Text)" }
            Write-Output "OK shim override corrupt fails closed."

            $wrongShimName = Get-ShimBinaryName $wrongRid
            $wrongShimDir = Join-Path $work "wrong-arch-shim"
            New-Item -ItemType Directory -Path $wrongShimDir -Force | Out-Null
            $wrongShimPath = Join-Path $wrongShimDir ("wrong-" + $wrongShimName)
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zipShim = [System.IO.Compression.ZipFile]::OpenRead($Package)
            try {
                $shimEntryName = "runtimes/$wrongRid/native/$wrongShimName"
                $shimEntry = $zipShim.Entries | Where-Object { $_.FullName -eq $shimEntryName } | Select-Object -First 1
                if ($null -eq $shimEntry) { Fail "package is missing $shimEntryName for shim wrong-arch probe" }
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($shimEntry, $wrongShimPath, $true)
            }
            finally { $zipShim.Dispose() }
            $env:MINIGRAFSHARP_SHIM_NATIVE_PATH = $wrongShimPath
            $shWrong = Invoke-Native $exe @()
            if ($shWrong.ExitCode -eq 0 -or $shWrong.Text -notmatch "MinigrafNativeLoadException") { Fail "shim override wrong-architecture did not fail closed :: $($shWrong.Text)" }
            Write-Output "OK shim override wrong-architecture ($wrongRid) fails closed."
        }
        finally {
            if ($null -eq $prevShimOverride) { Remove-Item Env:\MINIGRAFSHARP_SHIM_NATIVE_PATH -ErrorAction SilentlyContinue }
            else { $env:MINIGRAFSHARP_SHIM_NATIVE_PATH = $prevShimOverride }
        }
    }

    if ($Aot -or $Trim) {
        $publishArgs = @("publish", $csproj, "--configuration", "Release", "--framework", $TargetFramework, "--runtime", $Rid, "--self-contained", "true", "--nologo", "-p:TreatWarningsAsErrors=true", "-p:ILLinkTreatWarningsAsErrors=true")
        if ($Aot) { $publishArgs += "-p:PublishAot=true" } else { $publishArgs += "-p:PublishTrimmed=true" }
        $label = if ($Aot) { "NativeAOT" } else { "trimmed" }
        $publish = Invoke-Native "dotnet" $publishArgs
        if ($publish.ExitCode -ne 0) { Fail "$label publish failed for $Rid :: $($publish.Text)" }
        if ($publish.Text -match "warning IL[0-9]{4}") { Fail "$label publish emitted trim/AOT analysis warnings :: $($publish.Text)" }
        $published = Join-Path $work "bin/Release/$TargetFramework/$Rid/publish/Consumer"
        if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { $published += ".exe" }
        if (-not (Test-Path -LiteralPath $published)) { Fail "$label executable not found: $published" }
        $aotRun = Invoke-Native $published @()
        if ($aotRun.ExitCode -ne 0) { Fail "$label consumer exited $($aotRun.ExitCode) :: $($aotRun.Text)" }
        foreach ($marker in $markers) {
            if ($aotRun.Text -notmatch [regex]::Escape($marker)) { Fail "$label consumer missing marker '$marker' :: $($aotRun.Text)" }
        }
        Write-Output "OK $label consumer ($TargetFramework, $Rid): all markers present"
    }
}
finally {
    if ($null -eq $previousPackages) { Remove-Item Env:\NUGET_PACKAGES -ErrorAction SilentlyContinue }
    else { $env:NUGET_PACKAGES = $previousPackages }
    if ($null -eq $previousNativePath) { Remove-Item Env:\MINIGRAFSHARP_NATIVE_PATH -ErrorAction SilentlyContinue }
    else { $env:MINIGRAFSHARP_NATIVE_PATH = $previousNativePath }
    if ($null -eq $previousShimPath) { Remove-Item Env:\MINIGRAFSHARP_SHIM_NATIVE_PATH -ErrorAction SilentlyContinue }
    else { $env:MINIGRAFSHARP_SHIM_NATIVE_PATH = $previousShimPath }
    $resolvedWork = [System.IO.Path]::GetFullPath($work)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedWork.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([System.IO.Path]::GetFileName($resolvedWork)).StartsWith("minigrafsharp-consumer-", [StringComparison]::Ordinal)) {
        Fail "refusing cleanup outside the consumer temporary directory"
    }
    # Bounded retry: Windows can hold transient handles on just-exited
    # consumer images. Never let cleanup mask the primary failure.
    $cleanupError = $null
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction Stop
            $cleanupError = $null
            break
        }
        catch {
            $cleanupError = $_
            Start-Sleep -Milliseconds 300
        }
    }
    if ($null -ne $cleanupError) {
        Write-Output "cleanup: incomplete :: $($cleanupError.Exception.Message)"
    }
}

Write-Output "Packaged-consumer test passed ($TargetFramework)."
# The intended-failure probes (missing/override) leave a non-zero $LASTEXITCODE;
# exit explicitly so a fully successful run reports success.
exit 0
