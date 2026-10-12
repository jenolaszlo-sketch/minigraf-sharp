// M1a integration tests: raw execution boundary on the real engine.
// The native loader caches per process, so the missing-asset test must run
// FIRST. Override-path loading is proven in a child process (fresh loader).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
            using var execDb = MinigrafDatabase.OpenMemory();
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

// 8. Null input is a managed argument error (no native call).
try
{
    using var nullDb = MinigrafDatabase.OpenMemory();
    Check(Throws<ArgumentNullException>(() => nullDb.ExecuteRaw(null!)), "null datalog rejected");
}
catch (Exception ex)
{
    Check(false, $"null datalog rejected ({ex.GetType().Name}: {ex.Message})");
}

// 9. Use after dispose never reaches the released native handle.
try
{
    var disposedDb = MinigrafDatabase.OpenMemory();
    disposedDb.Dispose();
    Check(
        Throws<ObjectDisposedException>(() => disposedDb.ExecuteRaw("(query [:find ?x :where [?e :name ?x]])")),
        "disposed ExecuteRaw throws ObjectDisposedException");
}
catch (Exception ex)
{
    Check(false, $"disposed ExecuteRaw throws ObjectDisposedException ({ex.GetType().Name}: {ex.Message})");
}

// 10. Transact returns the native transaction JSON shape (raw, unparsed by production code).
using var execDb = MinigrafDatabase.OpenMemory();
string transactJson = execDb.ExecuteRaw("""(transact [[:alice :name "Alice"]])""");
Check(transactJson.Length != 0, "transact returns non-empty raw JSON");
try
{
    using JsonDocument txDoc = JsonDocument.Parse(transactJson);
    Check(txDoc.RootElement.TryGetProperty("transacted", out _), "transact JSON has transacted shape");
}
catch (Exception ex)
{
    Check(false, $"transact JSON has transacted shape ({ex.GetType().Name}: {ex.Message})");
}

// 11. Query raw JSON contains the transacted fact.
string queryJson = execDb.ExecuteRaw("(query [:find ?name :where [?e :name ?name]])");
Check(queryJson.Contains("Alice", StringComparison.Ordinal), "query raw JSON contains Alice");

// 12. Unicode round-trips exactly through UTF-8 in and out.
const string UnicodeValue = "Zürich ❄ 日本語 🙂";
execDb.ExecuteRaw($"""(transact [[:u1 :place/label "{UnicodeValue}"]])""");
string unicodeJson = execDb.ExecuteRaw("(query [:find ?v :where [?e :place/label ?v]])");
bool unicodeFound = false;
try
{
    using JsonDocument uniDoc = JsonDocument.Parse(unicodeJson);
    foreach (JsonElement row in uniDoc.RootElement.GetProperty("results").EnumerateArray())
    {
        foreach (JsonElement cell in row.EnumerateArray())
        {
            if (cell.ValueKind == JsonValueKind.String && cell.GetString() == UnicodeValue)
            {
                unicodeFound = true;
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  unicode query parse failed: {ex.GetType().Name}: {ex.Message}");
}
Check(unicodeFound, "unicode value round-trips exactly");

// 13. Malformed Datalog becomes a managed execution exception with a diagnostic.
const string BadDatalog = "(this is not valid datalog {{{)";
string badMessage = "";
bool badThrew = false;
try
{
    execDb.ExecuteRaw(BadDatalog);
}
catch (MinigrafExecutionException ex)
{
    badThrew = true;
    badMessage = ex.Message;
}
Check(badThrew, "malformed datalog throws MinigrafExecutionException");
Check(badMessage.Length != 0, "execution exception carries native diagnostic");

// 14. Repeated failures stay predictable; later valid calls still succeed.
int diagnosed = 0;
for (int i = 0; i < 50; i++)
{
    try
    {
        execDb.ExecuteRaw(BadDatalog);
    }
    catch (MinigrafExecutionException ex)
    {
        if (ex.Message.Length != 0)
        {
            diagnosed++;
        }
    }
}
Check(diagnosed == 50, "repeated malformed calls fail predictably");
string afterFail = execDb.ExecuteRaw("""(transact [[:bob :name "Bob"]])""");
Check(afterFail.Contains("transacted", StringComparison.Ordinal), "valid call succeeds after failures");

// 15. Alternating invalid/valid does not poison the handle.
bool alternating = true;
for (int i = 0; i < 20; i++)
{
    try
    {
        execDb.ExecuteRaw(BadDatalog);
        alternating = false;
    }
    catch (MinigrafExecutionException)
    {
    }

    string probeJson = execDb.ExecuteRaw("(query [:find ?name :where [?e :name ?name]])");
    if (!probeJson.Contains("Bob", StringComparison.Ordinal))
    {
        alternating = false;
    }
}
Check(alternating, "alternating invalid/valid does not poison handle");

// 16. Repeated execution sanity (owned-string free on every success path).
try
{
    using var stressDb = MinigrafDatabase.OpenMemory();
    for (int i = 0; i < 1000; i++)
    {
        if (i % 2 == 0)
        {
            stressDb.ExecuteRaw($"""(transact [[:s{i} :name "N-{i}"]])""");
        }
        else
        {
            stressDb.ExecuteRaw("(query [:find ?name :where [?e :name ?name]])");
        }
    }
    Check(true, "1000 mixed ExecuteRaw calls succeed");
}
catch (Exception ex)
{
    Check(false, $"1000 mixed ExecuteRaw calls succeed ({ex.GetType().Name}: {ex.Message})");
}

// 17. Error-capture race: failures must keep a non-empty diagnostic while
// siblings succeed on the same handle (per-database gate, no global lock).
try
{
    using var raceDb = MinigrafDatabase.OpenMemory();
    int okCount = 0;
    int raceDiagnosed = 0;
    int emptyDiag = 0;
    int unexpected = 0;
    Parallel.For(0, 8, task =>
    {
        for (int i = 0; i < 120; i++)
        {
            if (i % 2 == 0)
            {
                try
                {
                    raceDb.ExecuteRaw("(query [:find ?x :where [?e :name ?x]])");
                    Interlocked.Increment(ref okCount);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref unexpected);
                }
            }
            else
            {
                try
                {
                    raceDb.ExecuteRaw($"(definitely not datalog {task}-{i} {{{{");
                    Interlocked.Increment(ref unexpected);
                }
                catch (MinigrafExecutionException ex)
                {
                    if (ex.Message.Length != 0)
                    {
                        Interlocked.Increment(ref raceDiagnosed);
                    }
                    else
                    {
                        Interlocked.Increment(ref emptyDiag);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref unexpected);
                }
            }
        }
    });
    Check(okCount == 480, $"racy valid calls all succeed ({okCount}/480)");
    Check(raceDiagnosed == 480, $"racy failures all diagnosed ({raceDiagnosed}/480)");
    Check(emptyDiag == 0, "no empty failure diagnostic");
    Check(unexpected == 0, "no unexpected outcome");
}
catch (Exception ex)
{
    Check(false, $"error-capture race behaves ({ex.GetType().Name}: {ex.Message})");
}

Console.WriteLine(failures == 0 ? "ALL INTEGRATION TESTS PASSED" : $"{failures} INTEGRATION TEST(S) FAILED");
return failures == 0 ? 0 : 1;
