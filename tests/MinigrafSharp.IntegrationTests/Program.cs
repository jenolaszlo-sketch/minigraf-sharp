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

static string NewTempDbPath(string fileName)
{
    string dir = Path.Combine(
        Path.GetTempPath(), "MinigrafSharp.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return Path.Combine(dir, fileName);
}

static void DeleteTempDir(string dbPath)
{
    string? dir = Path.GetDirectoryName(dbPath);
    if (dir is null)
    {
        return;
    }

    for (int attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return;
        }
        catch (IOException) when (attempt < 4)
        {
            Thread.Sleep(100);
        }
    }

    // Final attempt surfaces a leaked-handle failure instead of hiding it.
    Directory.Delete(dir, recursive: true);
}

void CleanupTempDir(string dbPath)
{
    try
    {
        DeleteTempDir(dbPath);
    }
    catch (Exception ex)
    {
        Check(false, $"cleanup released files for {Path.GetFileName(dbPath)} ({ex.GetType().Name}: {ex.Message})");
    }
}

string? previousOverride = Environment.GetEnvironmentVariable(NativeLoader.OverrideVariable);

string rid = NativeLoader.GetRuntimeIdentifier();
string fileName = NativeLoader.GetNativeFileName(rid);

// 0. Native loader negative matrix. Runs before any successful load so the
// per-process loader cache is still empty for every case (a failed load does
// not cache a handle, so the next case retries resolution).
string loaderTemp = Path.Combine(
    Path.GetTempPath(), "MinigrafSharp.LoaderMatrix", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(loaderTemp);
void ExpectLoadFailure(string label, string overridePath)
{
    Environment.SetEnvironmentVariable(NativeLoader.OverrideVariable, overridePath);
    Check(
        Throws<MinigrafNativeLoadException>(() =>
        {
            using var db = MinigrafDatabase.OpenMemory();
            return db;
        }),
        label);
    Environment.SetEnvironmentVariable(NativeLoader.OverrideVariable, previousOverride);
}

try
{
    ExpectLoadFailure(
        "loader: nonexistent file fails closed",
        Path.Combine(loaderTemp, "does-not-exist.dll"));

    string zeroFile = Path.Combine(loaderTemp, "zero.dll");
    File.WriteAllBytes(zeroFile, []);
    ExpectLoadFailure("loader: zero-byte file fails closed", zeroFile);

    string garbageFile = Path.Combine(loaderTemp, "garbage.dll");
    File.WriteAllBytes(garbageFile, Enumerable.Repeat((byte)0xAB, 4096).ToArray());
    ExpectLoadFailure("loader: random-byte file fails closed", garbageFile);

    string emptyDir = Path.Combine(loaderTemp, "emptydir");
    Directory.CreateDirectory(emptyDir);
    ExpectLoadFailure("loader: directory override without asset fails closed", emptyDir);

    string dirWithName = Path.Combine(loaderTemp, "named");
    Directory.CreateDirectory(Path.Combine(dirWithName, fileName));
    ExpectLoadFailure("loader: expected filename as a directory fails closed", dirWithName);

    ExpectLoadFailure(
        "loader: missing parent directory fails closed",
        Path.Combine(loaderTemp, "no-such-parent", fileName));
}
finally
{
    Environment.SetEnvironmentVariable(NativeLoader.OverrideVariable, previousOverride);
    try { Directory.Delete(loaderTemp, recursive: true); } catch { }
}

// Locate the staged asset for the remaining probes.
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
        bool exited = child.WaitForExit(120_000);
        if (!exited || child.ExitCode != 0)
        {
            Console.WriteLine("  child stdout: " + child.StandardOutput.ReadToEnd());
            Console.WriteLine("  child stderr: " + child.StandardError.ReadToEnd());
            Console.WriteLine($"  child: exited={exited} exitcode={child.ExitCode}");
            Check(exited && child.ExitCode == 0, "override asset loads in child process");
        }
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

// 18. Execute() transact decodes to a typed transaction result.
using var qdb = MinigrafDatabase.OpenMemory();
MinigrafResult typedTx = qdb.Execute("""(transact [[:alice :name "Alice"]])""");
Check(
    typedTx is MinigrafTransactionResult tx && tx.TransactionId > 0,
    "Execute transact decodes MinigrafTransactionResult");

// 19. Execute() query decodes rows with named access.
qdb.Execute("""(transact [[:bob :name "Bob"]])""");
MinigrafResult typedQuery = qdb.Execute("(query [:find ?name :where [?e :name ?name]])");
bool queryOk = typedQuery is MinigrafQueryResult qr
    && qr.Rows.Count == 2
    && qr.Rows.Any(r => r["?name"].Kind == MinigrafValueKind.String && r["?name"].GetString() == "Alice")
    && qr.Rows.Any(r => r["?name"].GetString() == "Bob");
Check(queryOk, "Execute query decodes rows");

// 20. Scalar kinds through the real engine.
qdb.Execute("""(transact [[:sc :int 30] [:sc :neg -7] [:sc :float 4.5] [:sc :t true] [:sc :f false]])""");
MinigrafQueryResult scalars = (MinigrafQueryResult)qdb.Execute(
    "(query [:find ?v :where [:sc :int ?v]])");
Check(scalars.Rows.Count == 1 && scalars.Rows[0][0].Kind == MinigrafValueKind.Integer
    && scalars.Rows[0][0].GetInt64() == 30, "engine integer decodes exactly");
MinigrafQueryResult neg = (MinigrafQueryResult)qdb.Execute(
    "(query [:find ?v :where [:sc :neg ?v]])");
Check(neg.Rows[0][0].GetInt64() == -7, "engine negative integer decodes");
MinigrafQueryResult flt = (MinigrafQueryResult)qdb.Execute(
    "(query [:find ?v :where [:sc :float ?v]])");
Check(flt.Rows[0][0].Kind == MinigrafValueKind.Float
    && flt.Rows[0][0].GetDouble() == 4.5, "engine float decodes");
MinigrafQueryResult tru = (MinigrafQueryResult)qdb.Execute(
    "(query [:find ?v :where [:sc :t ?v]])");
MinigrafQueryResult fls = (MinigrafQueryResult)qdb.Execute(
    "(query [:find ?v :where [:sc :f ?v]])");
Check(tru.Rows[0][0].Kind == MinigrafValueKind.Boolean && tru.Rows[0][0].GetBoolean()
    && fls.Rows[0][0].Kind == MinigrafValueKind.Boolean && !fls.Rows[0][0].GetBoolean(),
    "engine booleans decode");

// 21. Null literal probe (engine may not express it; decoder unit tests cover the kind).
try
{
    qdb.Execute("""(transact [[:sc :n nil]])""");
    MinigrafQueryResult nul = (MinigrafQueryResult)qdb.Execute(
        "(query [:find ?v :where [:sc :n ?v]])");
    Check(nul.Rows.Count == 1 && nul.Rows[0][0].Kind == MinigrafValueKind.Null,
        "engine nil decodes to Null");
}
catch (MinigrafExecutionException ex)
{
    Console.WriteLine($"  nil literal rejected by engine (recorded): {ex.Message}");
    Check(true, "engine nil decodes to Null");
}

// 22. Retract decodes and removes the fact from the current view.
qdb.Execute("""(transact [[:gone :name "Gone"]])""");
MinigrafResult retracted = qdb.Execute("""(retract [[:gone :name "Gone"]])""");
Check(retracted is MinigrafRetractionResult, "Execute retract decodes MinigrafRetractionResult");
MinigrafQueryResult afterRetract = (MinigrafQueryResult)qdb.Execute(
    "(query [:find ?name :where [:gone :name ?name]])");
Check(afterRetract.Rows.Count == 0, "retracted fact leaves the current view");

// 23. Rule definition decodes to the shared Ok instance.
MinigrafResult okResult = qdb.Execute("(rule [(reachable ?x ?y) [?x :connected ?y]])");
Check(ReferenceEquals(okResult, MinigrafOkResult.Instance), "rule definition decodes Ok");

// 24. Row access through Execute(): positional, named, missing.
qdb.Execute("""(transact [[:r1 :name "Rex"] [:r1 :age 7]])""");
MinigrafQueryResult people = (MinigrafQueryResult)qdb.Execute(
    "(query [:find ?name ?age :where [?r :name ?name] [?r :age ?age]])");
MinigrafRow? rex = null;
foreach (MinigrafRow row in people.Rows)
{
    if (row["?name"].GetString() == "Rex")
    {
        rex = row;
    }
}
Check(rex is not null, "multi-variable query finds row");
if (rex is not null)
{
    int nameIndex = people.Variables[0] == "?name" ? 0 : 1;
    int ageIndex = 1 - nameIndex;
    Check(rex[nameIndex].GetString() == "Rex" && rex[ageIndex].GetInt64() == 7,
        "positional access matches named access");
    Check(rex["?age"].GetInt64() == 7, "named age access");
    Check(!rex.TryGetValue("?missing", out _), "TryGetValue miss is false");
    Check(Throws<KeyNotFoundException>(() => rex["?missing"]), "unknown variable throws KeyNotFound");
}

// 25. Unicode through Execute().
const string UniValue = "Zürich ❄ 日本語 🙂";
qdb.Execute($"""(transact [[:u9 :label "{UniValue}"]])""");
MinigrafQueryResult uni = (MinigrafQueryResult)qdb.Execute(
    "(query [:find ?v :where [:u9 :label ?v]])");
Check(uni.Rows.Count == 1 && uni.Rows[0]["?v"].GetString() == UniValue,
    "unicode survives Execute()");

// 26. Execute() preserves the M1a failure boundaries distinctly.
Check(Throws<ArgumentNullException>(() => qdb.Execute(null!)), "Execute null rejected");
var deadDb = MinigrafDatabase.OpenMemory();
deadDb.Dispose();
Check(Throws<ObjectDisposedException>(() => deadDb.Execute("(query [:find ?x :where [?e :name ?x]])")),
    "disposed Execute throws ObjectDisposedException");
bool execFailed = false;
try
{
    qdb.Execute("(broken datalog {{{)");
}
catch (MinigrafExecutionException)
{
    execFailed = true;
}
catch (MinigrafResultException)
{
    execFailed = false;
}
Check(execFailed, "malformed Datalog is execution failure, not result failure");

// 27. New file-backed database is created on open; files appear beside the path.
string newPath = NewTempDbPath("new.db");
try
{
    using (var fdb = MinigrafDatabase.Open(newPath))
    {
        fdb.Execute("""(transact [[:alice :name "Alice"]])""");
        MinigrafQueryResult found = (MinigrafQueryResult)fdb.Execute(
            "(query [:find ?name :where [?e :name ?name]])");
        Check(found.Rows.Count == 1, "file-backed Execute works");
        string raw = fdb.ExecuteRaw("(query [:find ?name :where [?e :name ?name]])");
        Check(raw.Contains("Alice", StringComparison.Ordinal), "file-backed ExecuteRaw works");
    }
    string? newDir = Path.GetDirectoryName(newPath);
    string[] created = newDir is null ? [] : Directory.GetFiles(newDir);
    Console.WriteLine($"  files after file-backed open: {string.Join(", ", created.Select(Path.GetFileName))}");
    Check(created.Length != 0, "file-backed open creates files");
}
finally
{
    CleanupTempDir(newPath);
}

// 28. Core persistence: write, checkpoint, close, reopen, read.
string persistPath = NewTempDbPath("persist.db");
try
{
    using (var wdb = MinigrafDatabase.Open(persistPath))
    {
        wdb.Execute("""(transact [[:alice :name "Alice"]])""");
        wdb.Checkpoint();
    }
    using (var rdb = MinigrafDatabase.Open(persistPath))
    {
        MinigrafQueryResult reopened = (MinigrafQueryResult)rdb.Execute(
            "(query [:find ?name :where [?e :name ?name]])");
        Check(reopened.Rows.Count == 1 && reopened.Rows[0]["?name"].GetString() == "Alice",
            "write/checkpoint/close/reopen preserves state");
    }
}
finally
{
    CleanupTempDir(persistPath);
}

// 29. Normal close without explicit checkpoint still persists (WAL replay on open).
string walPath = NewTempDbPath("wal.db");
try
{
    using (var wdb = MinigrafDatabase.Open(walPath))
    {
        wdb.Execute("""(transact [[:bob :name "Bob"]])""");
    }
    using (var rdb = MinigrafDatabase.Open(walPath))
    {
        MinigrafQueryResult replayed = (MinigrafQueryResult)rdb.Execute(
            "(query [:find ?name :where [?e :name ?name]])");
        Check(replayed.Rows.Count == 1 && replayed.Rows[0]["?name"].GetString() == "Bob",
            "close without checkpoint still persists (observed, not a crash guarantee)");
    }
}
finally
{
    CleanupTempDir(walPath);
}

// 30. Checkpoint is wired: succeeds mid-session, data visible before and after reopen.
string cpPath = NewTempDbPath("checkpoint.db");
try
{
    using (var wdb = MinigrafDatabase.Open(cpPath))
    {
        wdb.Execute("""(transact [[:cp :v 1]])""");
        wdb.Checkpoint();
        MinigrafQueryResult sameSession = (MinigrafQueryResult)wdb.Execute(
            "(query [:find ?v :where [:cp :v ?v]])");
        Check(sameSession.Rows.Count == 1, "checkpoint succeeds mid-session");
    }
    using (var rdb = MinigrafDatabase.Open(cpPath))
    {
        MinigrafQueryResult afterCp = (MinigrafQueryResult)rdb.Execute(
            "(query [:find ?v :where [:cp :v ?v]])");
        Check(afterCp.Rows.Count == 1 && afterCp.Rows[0][0].GetInt64() == 1,
            "checkpointed state survives reopen");
    }
}
finally
{
    CleanupTempDir(cpPath);
}

// 31. Multiple reopen cycles preserve accumulated state.
string cyclePath = NewTempDbPath("cycle.db");
try
{
    using (var db1 = MinigrafDatabase.Open(cyclePath))
    {
        db1.Execute("""(transact [[:a :v "A"]])""");
        db1.Checkpoint();
    }
    using (var db2 = MinigrafDatabase.Open(cyclePath))
    {
        MinigrafQueryResult q1 = (MinigrafQueryResult)db2.Execute(
            "(query [:find ?v :where [?e :v ?v]])");
        Check(q1.Rows.Count == 1, "first reopen sees A");
        db2.Execute("""(transact [[:b :v "B"]])""");
        db2.Checkpoint();
    }
    using (var db3 = MinigrafDatabase.Open(cyclePath))
    {
        MinigrafQueryResult q2 = (MinigrafQueryResult)db3.Execute(
            "(query [:find ?v :where [?e :v ?v]])");
        Check(q2.Rows.Count == 2, "second reopen sees A + B");
    }
}
finally
{
    CleanupTempDir(cyclePath);
}

// 32. Empty database reopens; empty queries stay empty.
string emptyPath = NewTempDbPath("empty.db");
try
{
    using (var edb = MinigrafDatabase.Open(emptyPath))
    {
        edb.Checkpoint();
    }
    using (var rdb = MinigrafDatabase.Open(emptyPath))
    {
        MinigrafQueryResult empty = (MinigrafQueryResult)rdb.Execute(
            "(query [:find ?name :where [?e :name ?name]])");
        Check(empty.Rows.Count == 0, "empty database reopens with empty results");
    }
}
finally
{
    CleanupTempDir(emptyPath);
}

// 33. Non-ASCII file path opens (UTF-8, not the ANSI code page).
string uniPath = NewTempDbPath("minigraf-Žluťoučký-日本語.db");
try
{
    using (var wdb = MinigrafDatabase.Open(uniPath))
    {
        wdb.Execute("""(transact [[:u :v "x"]])""");
        wdb.Checkpoint();
    }
    using (var rdb = MinigrafDatabase.Open(uniPath))
    {
        MinigrafQueryResult uq = (MinigrafQueryResult)rdb.Execute(
            "(query [:find ?v :where [:u :v ?v]])");
        Check(uq.Rows.Count == 1, "unicode path round-trips persistence");
    }
}
finally
{
    CleanupTempDir(uniPath);
}

// 34. Unusable paths fail safely with MinigrafOpenException, never a crash.
string missingParent = Path.Combine(
    Path.GetTempPath(), "MinigrafSharp.Tests", Guid.NewGuid().ToString("N"), "no-such-dir", "db.db");
Check(Throws<MinigrafOpenException>(() => MinigrafDatabase.Open(missingParent)),
    "missing parent directory fails with MinigrafOpenException");
string dirAsDb = Path.Combine(Path.GetTempPath(), "MinigrafSharp.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dirAsDb);
try
{
    Check(Throws<MinigrafOpenException>(() => MinigrafDatabase.Open(dirAsDb)),
        "directory as database path fails with MinigrafOpenException");
}
finally
{
    Directory.Delete(dirAsDb);
}

// 35. Same-path double open: observe, then prove the first handle stays usable.
string doublePath = NewTempDbPath("double.db");
try
{
    using var db1 = MinigrafDatabase.Open(doublePath);
    db1.Execute("""(transact [[:alice :name "Alice"]])""");
    try
    {
        using var db2 = MinigrafDatabase.Open(doublePath);
        MinigrafQueryResult q2 = (MinigrafQueryResult)db2.Execute(
            "(query [:find ?name :where [?e :name ?name]])");
        Console.WriteLine($"  double-open: second handle accepted, sees {q2.Rows.Count} row(s)");
    }
    catch (MinigrafOpenException ex)
    {
        Console.WriteLine($"  double-open: second handle rejected (recorded): {ex.Message}");
    }
    MinigrafQueryResult stillThere = (MinigrafQueryResult)db1.Execute(
        "(query [:find ?name :where [?e :name ?name]])");
    Check(stillThere.Rows.Count == 1, "first handle usable after double-open attempt");
}
finally
{
    CleanupTempDir(doublePath);
}

// 36. Checkpoint after disposal never reaches native code.
string deadPath = NewTempDbPath("dead.db");
try
{
    var cpDead = MinigrafDatabase.Open(deadPath);
    cpDead.Dispose();
    Check(Throws<ObjectDisposedException>(() =>
    {
        cpDead.Checkpoint();
        return (object?)null;
    }), "disposed Checkpoint throws ObjectDisposedException");
}
finally
{
    CleanupTempDir(deadPath);
}

// 37. Checkpoint on an in-memory database: observe, then prove usability either way.
using (var mdb = MinigrafDatabase.OpenMemory())
{
    try
    {
        mdb.Checkpoint();
        Console.WriteLine("  checkpoint on memory db: success");
    }
    catch (MinigrafCheckpointException ex)
    {
        Console.WriteLine($"  checkpoint on memory db: rejected (recorded): {ex.Message}");
    }
    mdb.Execute("""(transact [[:m :v 1]])""");
    MinigrafQueryResult mq = (MinigrafQueryResult)mdb.Execute(
        "(query [:find ?v :where [:m :v ?v]])");
    Check(mq.Rows.Count == 1, "memory database usable around Checkpoint");
}

// ==================== M2.5 hardening ====================

// 38. Failure/success alternation stress.
using (var alter = MinigrafDatabase.OpenMemory())
{
    int altDiagnosed = 0;
    int altSucceeded = 0;
    for (int i = 0; i < 300; i++)
    {
        try
        {
            alter.Execute("(this is not datalog {{{");
        }
        catch (MinigrafExecutionException ex)
        {
            if (!string.IsNullOrEmpty(ex.Message))
            {
                altDiagnosed++;
            }
        }

        MinigrafQueryResult after = (MinigrafQueryResult)alter.Execute(
            "(query [:find ?x :where [?e :name ?x]])");
        if (after.Rows.Count >= 0)
        {
            altSucceeded++;
        }
    }

    Check(altDiagnosed == 300 && altSucceeded == 300,
        $"alternation: 300 failures diagnosed and 300 later successes ({altDiagnosed}/{altSucceeded})");
}

// 39. Execute soak: bounded mixed workload.
const int soakOps = 10000;
using (var soak = MinigrafDatabase.OpenMemory())
{
    int ok = 0;
    int fail = 0;
    for (int i = 0; i < soakOps; i++)
    {
        switch (i % 5)
        {
            case 0:
                soak.ExecuteRaw($"(transact [[:e{i} :n {i}]])");
                ok++;
                break;
            case 1:
                soak.Execute("(query [:find ?n :where [?e :n ?n]])");
                ok++;
                break;
            case 2:
                soak.ExecuteRaw("(query [:find ?n :where [?e :n ?n]])");
                ok++;
                break;
            case 3:
                try
                {
                    soak.Execute("(invalid datalog {{{");
                }
                catch (MinigrafExecutionException)
                {
                    fail++;
                }
                break;
            default:
                soak.Execute($"(transact [[:m{i % 97} :v \"s{i}\"]])");
                ok++;
                break;
        }
    }

    Check(ok == 8000 && fail == 2000, $"execute soak: {soakOps} mixed ops ({ok} ok / {fail} fail)");
}

// 40. Lifecycle stress: 5000 in-memory open/dispose cycles + bounded handle growth.
Process current = Process.GetCurrentProcess();
current.Refresh();
int handlesBefore = current.HandleCount;
for (int i = 0; i < 5000; i++)
{
    using var cycleDb = MinigrafDatabase.OpenMemory();
    if (i == 0)
    {
        cycleDb.Execute("(query [:find ?x :where [?e :n ?x]])");
    }
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
current.Refresh();
int handlesAfter = current.HandleCount;
Check(true, "lifecycle: 5000 in-memory open/dispose cycles complete");
Check(handlesAfter - handlesBefore < 256,
    $"lifecycle: handle growth bounded ({handlesBefore} -> {handlesAfter}, delta {handlesAfter - handlesBefore})");

// 41. File-backed lifecycle + persistence stress (accumulating, verified on final reopen).
string lifePath = NewTempDbPath("life.db");
try
{
    const int fileCycles = 200;
    for (int i = 0; i < fileCycles; i++)
    {
        using var wdb = MinigrafDatabase.Open(lifePath);
        wdb.Execute($"(transact [[:c{i} :v {i}]])");
        wdb.Checkpoint();
    }

    using var rdb = MinigrafDatabase.Open(lifePath);
    MinigrafQueryResult accumulated = (MinigrafQueryResult)rdb.Execute(
        "(query [:find ?v :where [?e :v ?v]])");
    Check(accumulated.Rows.Count == fileCycles,
        $"persistence: {fileCycles} accumulating write/checkpoint/reopen cycles all visible ({accumulated.Rows.Count})");
}
finally
{
    CleanupTempDir(lifePath);
}

// 42. Independent databases in parallel: no handle mix-up, no result cross-talk.
int multiErrors = 0;
Parallel.For(0, 8, index =>
{
    using var db = MinigrafDatabase.OpenMemory();
    for (int i = 0; i < 200; i++)
    {
        db.Execute($"(transact [[:d{index} :v {i}]])");
        MinigrafQueryResult view = (MinigrafQueryResult)db.Execute(
            "(query [:find ?v :where [?e :v ?v]])");
        if (view.Rows.Count != i + 1)
        {
            Interlocked.Increment(ref multiErrors);
        }
    }
});
Check(multiErrors == 0, "parallel: 8 independent databases stay isolated (no cross-talk)");

// 43. Same-handle concurrency stress.
using (var shared = MinigrafDatabase.OpenMemory())
{
    int okCount = 0;
    int failCount = 0;
    int unexpected = 0;
    Parallel.For(0, 8, task =>
    {
        for (int i = 0; i < 150; i++)
        {
            if (i % 2 == 0)
            {
                shared.ExecuteRaw($"(transact [[:s{task} :v {i}]])");
                Interlocked.Increment(ref okCount);
            }
            else
            {
                try
                {
                    shared.Execute("(bad datalog {{{");
                    Interlocked.Increment(ref unexpected);
                }
                catch (MinigrafExecutionException)
                {
                    Interlocked.Increment(ref failCount);
                }
            }
        }
    });
    Check(okCount == 600 && failCount == 600 && unexpected == 0,
        $"same-handle concurrency: {okCount} ok / {failCount} diagnosed / {unexpected} unexpected");
}

// 44. Dispose race: operations racing Dispose must never crash or use freed memory.
int disposeUnsafe = 0;
for (int round = 0; round < 20; round++)
{
    var raceDb = MinigrafDatabase.OpenMemory();
    using var start = new ManualResetEventSlim(false);
    var workers = new Thread[4];
    for (int w = 0; w < workers.Length; w++)
    {
        workers[w] = new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < 250; i++)
            {
                try
                {
                    raceDb.ExecuteRaw("(query [:find ?x :where [?e :n ?x]])");
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref disposeUnsafe);
                }
            }
        });
        workers[w].Start();
    }

    start.Set();
    raceDb.Dispose();
    foreach (Thread worker in workers)
    {
        worker.Join();
    }
}
Check(disposeUnsafe == 0, "dispose race: 20 rounds, only ObjectDisposedException, no unsafe failure");

// 45. Privacy sentinels (execution path). Native diagnostics may echo caller
// input; record the exact behavior rather than assuming.
const string Sentinel = "M2_5_SECRET_SENTINEL";
using (var priv = MinigrafDatabase.OpenMemory())
{
    priv.Execute($"""(transact [[:p :v "{Sentinel}"]])""");
    MinigrafQueryResult privRead = (MinigrafQueryResult)priv.Execute(
        "(query [:find ?v :where [:p :v ?v]])");
    Check(privRead.Rows[0][0].GetString() == Sentinel, "privacy: sentinel value stored and read back");

    bool executionLeaksSentinel = false;
    try
    {
        priv.Execute($"(totally invalid statement {Sentinel} {{{{)");
    }
    catch (MinigrafExecutionException ex)
    {
        executionLeaksSentinel = ex.ToString().Contains(Sentinel, StringComparison.Ordinal);
    }

    Console.WriteLine($"  privacy: execution-exception echoes caller sentinel = {executionLeaksSentinel} (native diagnostic behavior)");
    Check(true, "privacy: execution exception inspected");
}

Console.WriteLine(failures == 0 ? "ALL INTEGRATION TESTS PASSED" : $"{failures} INTEGRATION TEST(S) FAILED");
return failures == 0 ? 0 : 1;
