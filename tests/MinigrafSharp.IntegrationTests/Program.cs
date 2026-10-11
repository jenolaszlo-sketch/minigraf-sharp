// M0 integration tests: real pinned minigraf-c engine, no mocks.
// The native loader caches per process, so the missing-asset test must run
// FIRST. Override-path loading is proven in a child process (fresh loader).
using System.Diagnostics;
using System.Runtime.InteropServices;
using MinigrafSharp;
using MinigrafSharp.Native;

if (args.Length == 1 && args[0] == "--child-open")
{
    using var childDb = MinigrafDatabase.OpenMemory();
    return 0;
}

int failures = 0;

void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
    if (!condition)
    {
        failures++;
    }
}

static bool Throws<TException>(Func<object?> action)
    where TException : Exception
{
    try
    {
        action();
        return false;
    }
    catch (TException)
    {
        return true;
    }
}

string? previousOverride = Environment.GetEnvironmentVariable(NativeLoader.OverrideVariable);

// 0. Missing asset fails closed. Runs before any successful load.
Environment.SetEnvironmentVariable(
    NativeLoader.OverrideVariable,
    Path.Combine(Path.GetTempPath(), "minigraf-sharp-missing-native.dll"));
Check(
    Throws<MinigrafNativeLoadException>(() =>
    {
        using var db = MinigrafDatabase.OpenMemory();
        return db;
    }),
    "missing native asset fails closed");
Environment.SetEnvironmentVariable(NativeLoader.OverrideVariable, previousOverride);

// Locate the staged asset for the remaining probes.
string rid = NativeLoader.GetRuntimeIdentifier();
string fileName = NativeLoader.GetNativeFileName(rid);
string asset = "";
foreach (string candidate in NativeLoader.GetCandidatePaths(rid, fileName))
{
    if (File.Exists(candidate))
    {
        asset = candidate;
        break;
    }
}
Check(asset != "", $"staged native asset present for {rid}");
if (asset == "")
{
    Console.WriteLine("Stage it first: pwsh eng/Get-Native.ps1");
    return 1;
}
Console.WriteLine($"asset: {asset}");

// 1. Expected C exports exist on the staged binary.
string[] expectedExports =
[
    "minigraf_open",
    "minigraf_open_in_memory",
    "minigraf_close",
    "minigraf_execute",
    "minigraf_string_free",
    "minigraf_checkpoint",
    "minigraf_last_error",
];
nint probe = NativeLibrary.Load(asset);
try
{
    bool allPresent = true;
    foreach (string name in expectedExports)
    {
        if (!NativeLibrary.TryGetExport(probe, name, out _))
        {
            Console.WriteLine($"  missing export: {name}");
            allPresent = false;
        }
    }
    Check(allPresent, "expected minigraf-c exports present");
}
finally
{
    NativeLibrary.Free(probe);
}

// 2. OpenMemory through default probing, then dispose.
try
{
    using (var db = MinigrafDatabase.OpenMemory())
    {
        Check(true, "OpenMemory succeeds");
    }
    Check(true, "Dispose succeeds");
}
catch (Exception ex)
{
    Check(false, $"OpenMemory succeeds ({ex.GetType().Name}: {ex.Message})");
    Check(false, "Dispose succeeds");
}

// 3. Repeated dispose is safe.
try
{
    var db = MinigrafDatabase.OpenMemory();
    db.Dispose();
    db.Dispose();
    Check(true, "repeated dispose is safe");
}
catch (Exception ex)
{
    Check(false, $"repeated dispose is safe ({ex.GetType().Name}: {ex.Message})");
}

// 4. Repeated open/dispose cycles expose handle lifetime mistakes.
try
{
    for (int i = 0; i < 100; i++)
    {
        using var db = MinigrafDatabase.OpenMemory();
    }
    Check(true, "100 open/dispose cycles succeed");
}
catch (Exception ex)
{
    Check(false, $"100 open/dispose cycles succeed ({ex.GetType().Name}: {ex.Message})");
}

// 5. Concurrent open/dispose on one process (native handle serializes calls).
try
{
    Parallel.For(0, 8, _ =>
    {
        for (int i = 0; i < 25; i++)
        {
            using var db = MinigrafDatabase.OpenMemory();
        }
    });
    Check(true, "concurrent open/dispose succeeds");
}
catch (Exception ex)
{
    Check(false, $"concurrent open/dispose succeeds ({ex.GetType().Name}: {ex.Message})");
}

// 6. Override path loads in a fresh process.
try
{
    string? exe = Environment.ProcessPath;
    Check(exe is not null, "child probe executable known");
    if (exe is not null)
    {
        var psi = new ProcessStartInfo(exe, "--child-open")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment[NativeLoader.OverrideVariable] = asset;
        using var child = Process.Start(psi)!;
        child.WaitForExit(120_000);
        Check(child.ExitCode == 0, "override asset loads in child process");
    }
}
catch (Exception ex)
{
    Check(false, $"override asset loads in child process ({ex.GetType().Name}: {ex.Message})");
}

// 7. Loader diagnostics identify the loaded asset.
try
{
    string loaded = NativeLoader.LoadedPath;
    Check(loaded.EndsWith(fileName, StringComparison.OrdinalIgnoreCase), "LoadedPath reports staged asset");
    Console.WriteLine($"loaded: {loaded}");
}
catch (Exception ex)
{
    Check(false, $"LoadedPath reports staged asset ({ex.GetType().Name}: {ex.Message})");
}

Console.WriteLine(failures == 0 ? "ALL INTEGRATION TESTS PASSED" : $"{failures} INTEGRATION TEST(S) FAILED");
return failures == 0 ? 0 : 1;
