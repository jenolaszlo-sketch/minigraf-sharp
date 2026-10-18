using System.Runtime.InteropServices;
using MinigrafSharp;

internal static class Program
{
    private const string BobUuidText = "550e8400-e29b-41d4-a716-446655440001";
    private static readonly Guid BobUuid = Guid.Parse(BobUuidText);

    private static int _pass;
    private static int _fail;
    private static readonly List<string> FailingCases = [];
    private static readonly List<string> CleanupDirs = [];

    private static string RootTemp { get; } =
        Path.Combine(Path.GetTempPath(), "MinigrafSharp.IntegratedQualificationTests");

    private static void Main()
    {
        Console.WriteLine("integrated qualification (M3.2g):");
        Console.WriteLine("  surface: M3.1 prepared queries + M3.2 rich native surface together");
        Console.WriteLine("  scope: host RID, net8.0 and net10.0");

        Cases();
        Console.WriteLine($"pass={_pass} fail={_fail}");
        Cleanup();
        if (_fail != 0)
        {
            Console.WriteLine("failed case: " + string.Join(", ", FailingCases));
            Environment.Exit(1);
        }

        Console.WriteLine("ALL INTEGRATED-QUALIFICATION TESTS PASSED");
    }

    private static void Cases()
    {
        // Order invariant: the refusal probes must run before any successful
        // shim load in this process, because once the loader has a verified
        // handle it short-circuits. This is what makes "identity precedes
        // use" observable for every open entry point.
        Case("E/I9 identity refusal precedes use on every open path, then the verified shim executes", IdentityRefusalPrecedesUse);
        Case("A open with options, begin, write, commit, prepared query, reopen", CommitAndQuery);
        Case("B rollback discards the write and leaves the database usable", Rollback);
        Case("C failure recovery: structured error then a successful commit", FailureRecovery);
        Case("D typed Ref/Keyword written, decoded, and bound through a prepared query", TypedRoundTrip);
        Case("I1 write-transaction guard is thread-local across databases", ThreadLocalGuard);
        Case("I2 disposal rolls back without committing", DisposalRollsBack);
        Case("I3 prepared queries inside an open transaction see committed facts only", PreparedSeesCommittedOnly);
        Case("I4 transactional reads see staged and committed facts", TransactionalReadsSeeBoth);
        Case("I5 rule registration is not transactional", RulesNotTransactional);
        Case("I6 failed staging preserves the transaction's usable state", FailedStagingUsable);
        Case("I7 failed commit retains structured diagnostics after terminalization", FailedCommitDiagnostics);
        Case("I8 typed Ref/Keyword never become ordinary strings on the owned surface", TypedNeverStrings);
    }

    private static void IdentityRefusalPrecedesUse()
    {
        string wrong = OfficialAssetPath();
        Check(File.Exists(wrong), "official engine staged as the deliberately-wrong binary: " + wrong);

        string? previous = Environment.GetEnvironmentVariable("MINIGRAFSHARP_SHIM_NATIVE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("MINIGRAFSHARP_SHIM_NATIVE_PATH", wrong);

            // Every database entry point must identity-verify before use.
            Expect<MinigrafNativeLoadException>(
                () => MinigrafPreparedDatabase.OpenMemory(), "memory open refused");
            Expect<MinigrafNativeLoadException>(
                () => MinigrafPreparedDatabase.OpenMemory(MinigrafOpenOptions.Default),
                "memory options open refused");
            string refusedPath = FileFix("refusal");
            Expect<MinigrafNativeLoadException>(
                () => MinigrafPreparedDatabase.Open(refusedPath), "file open refused");
            Expect<MinigrafNativeLoadException>(
                () => MinigrafPreparedDatabase.Open(refusedPath, MinigrafOpenOptions.Default),
                "file options open refused");
            Expect<MinigrafNativeLoadException>(
                () => _ = MinigrafRuntimeIdentity.Current, "runtime identity refused");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MINIGRAFSHARP_SHIM_NATIVE_PATH", previous);
        }

        // A failed candidate cached nothing: the verified shim now loads and
        // executes within the same process.
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute("""(transact [[:ok :name "Ok"]])""");
        Check(Rows(db).Count == 1, "verified shim executes after the refusals");
        var identity = MinigrafRuntimeIdentity.Current;
        Check(identity.ShimAbiVersion == "0.1.0" && identity.CoreVersion == "2.0.7",
            "verified identity available after the refusals: " + identity.ShimAbiVersion +
            " over " + identity.CoreVersion);
    }

    private static void CommitAndQuery()
    {
        string path = FileFix("commit-query");
        var options = new MinigrafOpenOptions
        {
            Synchronous = MinigrafSyncMode.Normal,
            WalCheckpointThreshold = MinigrafOpenOptions.Unlimited,
        };

        using (var db = MinigrafPreparedDatabase.Open(path, options))
        {
            using (var tx = db.BeginWrite())
            {
                tx.Execute("""(transact [[:alice :name "Alice"] [:alice :age 30]])""");
                tx.Execute("""(transact [[:bob :name "Bob"] [:bob :age 25]])""");
                tx.Commit();
            }

            using var query = db.Prepare(
                "(query [:find ?name ?age :where [?e :name ?name] [?e :age ?age]])");
            var rows = (MinigrafQueryResult)query.Execute(new Dictionary<string, MinigrafBindValue>());
            var ages = new Dictionary<string, long>();
            foreach (MinigrafRow row in rows.Rows)
            {
                ages[row["?name"].GetString()!] = row["?age"].GetInt64();
            }

            Check(rows.Rows.Count == 2 && ages["Alice"] == 30 && ages["Bob"] == 25,
                "prepared query sees exactly the committed facts with their values");
        }

        Check(File.Exists(path + ".wal"), "Unlimited left the WAL for replay after close");
        using var reopened = MinigrafPreparedDatabase.Open(path);
        Check(Rows(reopened).Count == 2,
            "committed transaction survives close and reopen (WAL replay)");
    }

    private static void Rollback()
    {
        string path = FileFix("rollback");
        using (var db = MinigrafPreparedDatabase.Open(path))
        {
            using (var tx = db.BeginWrite())
            {
                tx.Execute("""(transact [[:staged :name "Staged"]])""");
                Check(Rows(tx).Count == 1, "transactional read sees the staged fact");
                tx.Rollback();
            }

            Check(Rows(db).Count == 0, "rolled-back write is absent from the database");

            using var again = db.BeginWrite();
            again.Execute("""(transact [[:kept :name "Kept"]])""");
            again.Commit();
            Check(Rows(db).Count == 1, "database remains usable after rollback");
        }

        using var reopened = MinigrafPreparedDatabase.Open(path);
        Check(Rows(reopened).Count == 1, "rollback discarded nothing that was later committed");
    }

    private static void FailureRecovery()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var tx = db.BeginWrite();
        tx.Execute("""(transact [[:before :name "Before"]])""");
        var error = Expect<MinigrafExecutionException>(
            () => tx.Execute("(transact [[:broken"), "invalid staging fails");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Parser,
            "structured category Parser, got " + error.NativeCategory);
        Check(error.NativeCode is not null && error.NativeCode.StartsWith("PRS-", StringComparison.Ordinal),
            "stable native code preserved, got " + error.NativeCode);
        tx.Execute("""(transact [[:after :name "After"]])""");
        tx.Commit();

        var rows = Rows(db, "(query [:find ?name :where [?e :name ?name]])");
        Check(rows.Count == 2, "only the valid stages committed, got " + rows.Count);
    }

    private static void TypedRoundTrip()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using (var tx = db.BeginWrite())
        {
            tx.Execute($$"""(transact [[:alice :name "Alice"] [:alice :friend #uuid "{{BobUuidText}}"] [:alice :status :Status/Active]])""");
            tx.Commit();
        }

        var friend = One(db, "(query [:find ?v :where [:alice :friend ?v]])");
        Check(friend.Kind == MinigrafValueKind.Ref && friend.GetRef() == BobUuid,
            "written ref decodes typed with UUID identity: " + friend);
        var status = One(db, "(query [:find ?v :where [:alice :status ?v]])");
        Check(status.Kind == MinigrafValueKind.Keyword && status.GetKeyword() == ":Status/Active",
            "written keyword decodes typed with exact text: " + status);

        using var byRef = db.Prepare("(query [:find ?n :where [?e :friend $fr] [?e :name ?n]])");
        var refRows = (MinigrafQueryResult)byRef.Execute(new Dictionary<string, MinigrafBindValue>
        {
            ["fr"] = MinigrafBindValue.ValRef(BobUuid),
        });
        Check(refRows.Rows.Count == 1 && refRows.Rows[0]["?n"].GetString() == "Alice",
            "decoded ref value binds through a prepared query by identity");

        using var byKeyword = db.Prepare("(query [:find ?e :where [?e :status $kw]])");
        var kwRows = (MinigrafQueryResult)byKeyword.Execute(new Dictionary<string, MinigrafBindValue>
        {
            ["kw"] = MinigrafBindValue.ValKeyword(":Status/Active"),
        });
        Check(kwRows.Rows.Count == 1 && kwRows.Rows[0]["?e"].Kind == MinigrafValueKind.Ref &&
              kwRows.Rows[0]["?e"].GetRef() == EntityOf(db),
            "decoded keyword value binds through a prepared query; the result is a typed ref");

        var sameRef = One(db, "(query [:find ?v :where [:alice :friend ?v]])");
        Check(friend.Equals(sameRef) && friend.GetHashCode() == sameRef.GetHashCode(),
            "two decodes of the same ref are equal and hash equal");
    }

    private static void ThreadLocalGuard()
    {
        using var first = MinigrafPreparedDatabase.OpenMemory();
        using var second = MinigrafPreparedDatabase.OpenMemory();
        using (var tx = first.BeginWrite())
        {
            tx.Execute("""(transact [[:a :v 1]])""");
            Expect<InvalidOperationException>(
                () => second.BeginWrite(),
                "same-thread second transaction on another database refused");
            tx.Rollback();
        }

        using var other = second.BeginWrite();
        other.Execute("""(transact [[:b :v 2]])""");
        other.Commit();
        Check(Rows(second, "(query [:find ?e :where [?e :v ?v]])").Count == 1,
            "second database usable once the first transaction terminalized");
    }

    private static void DisposalRollsBack()
    {
        string path = FileFix("dispose");
        using (var db = MinigrafPreparedDatabase.Open(path))
        {
            var tx = db.BeginWrite();
            tx.Execute("""(transact [[:a :v 1]])""");
            tx.Dispose();
            Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 0,
                "dispose did not commit");
        }

        using var reopened = MinigrafPreparedDatabase.Open(path);
        Check(Rows(reopened, "(query [:find ?e :where [?e :v ?v]])").Count == 0,
            "dispose rollback is durable across reopen");
    }

    private static void PreparedSeesCommittedOnly()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute("""(transact [[:committed :name "Committed"]])""");
        using var prepared = db.Prepare("(query [:find ?e :where [?e :name ?n]])");
        using (var tx = db.BeginWrite())
        {
            tx.Execute("""(transact [[:staged :name "Staged"]])""");
            var preparedRows = (MinigrafQueryResult)prepared.Execute(new Dictionary<string, MinigrafBindValue>());
            Check(preparedRows.Rows.Count == 1,
                "prepared execution sees committed facts only, got " + preparedRows.Rows.Count);
            tx.Rollback();
        }
    }

    private static void TransactionalReadsSeeBoth()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute("""(transact [[:committed :name "Committed"]])""");
        using var tx = db.BeginWrite();
        tx.Execute("""(transact [[:staged :name "Staged"]])""");
        var rows = Rows(tx, "(query [:find ?e :where [?e :name ?n]])");
        Check(rows.Count == 2, "transactional read sees committed and staged facts, got " + rows.Count);
        tx.Rollback();
    }

    private static void RulesNotTransactional()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute("""(transact [[:a :edge :b]])""");
        using (var tx = db.BeginWrite())
        {
            tx.Execute("(rule [(reach ?x ?y) [?x :edge ?y]])");
            tx.Execute("""(transact [[:b :edge :c]])""");
            tx.Rollback();
        }

        var rows = Rows(db, "(query [:find ?y :where (reach :a ?y)])");
        Check(rows.Count == 1 && rows[0]["?y"].Kind == MinigrafValueKind.Keyword &&
              rows[0]["?y"].GetKeyword() == ":b",
            "rule registered inside the rolled-back transaction persists and sees committed facts only");
    }

    private static void FailedStagingUsable()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        _ = Expect<MinigrafExecutionException>(
            () => tx.Execute("(transact [[:broken"), "invalid staging rejected");
        tx.Execute("""(transact [[:b :v 2]])""");
        tx.Commit();
        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 2,
            "both valid stages survive the failed staging");
    }

    private static void FailedCommitDiagnostics()
    {
        string path = FileFix("failed-commit");
        using var db = MinigrafPreparedDatabase.Open(path);
        string wal = path + ".wal";
        _ = Directory.CreateDirectory(wal);
        try
        {
            var tx = db.BeginWrite();
            tx.Execute("""(transact [[:a :v 1]])""");
            var error = Expect<MinigrafExecutionException>(tx.Commit, "commit fails with the WAL blocked");
            Check(error.NativeCategory == MinigrafNativeErrorCategory.Internal,
                "category Internal, got " + error.NativeCategory);
            Check(error.NativeCode == "INT-000", "measured INT-000, got " + error.NativeCode);
            Check(!string.IsNullOrWhiteSpace(error.Message),
                "diagnostics retained after terminalization: " + error.Message);
            Expect<ObjectDisposedException>(
                () => tx.Execute("""(transact [[:b :v 2]])"""), "failed commit is terminal");
            Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 0,
                "failed commit applied nothing");
            tx.Dispose();
        }
        finally
        {
            Directory.Delete(wal);
        }

        using (var retry = db.BeginWrite())
        {
            retry.Execute("""(transact [[:c :v 3]])""");
            retry.Commit();
        }

        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 1,
            "database usable and correct after the failed commit");
    }

    private static void TypedNeverStrings()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute($$"""(transact [[:alice :friend #uuid "{{BobUuidText}}"] [:alice :status :Status/Active]])""");

        string raw = db.ExecuteRaw("(query [:find ?v :where [:alice :friend ?v]])");
        Check(raw.Contains("{\"type\":\"ref\",\"value\":\"" + BobUuidText + "\"}", StringComparison.Ordinal),
            "owned surface emits a typed ref envelope: " + raw);

        var friend = One(db, "(query [:find ?v :where [:alice :friend ?v]])");
        Check(friend.Kind == MinigrafValueKind.Ref, "ref is not an ordinary string: " + friend.Kind);
        Expect<InvalidOperationException>(() => friend.GetString(), "GetString rejects a Ref");
        var status = One(db, "(query [:find ?v :where [:alice :status ?v]])");
        Check(status.Kind == MinigrafValueKind.Keyword, "keyword is not an ordinary string: " + status.Kind);
        Expect<InvalidOperationException>(() => status.GetString(), "GetString rejects a Keyword");
    }

    // --- helpers ---------------------------------------------------------

    private static IReadOnlyList<MinigrafRow> Rows(MinigrafPreparedDatabase db, string query =
        "(query [:find ?name :where [?e :name ?name]])") =>
        ((MinigrafQueryResult)db.Execute(query)).Rows;

    private static IReadOnlyList<MinigrafRow> Rows(MinigrafWriteTransaction tx, string query =
        "(query [:find ?name :where [?e :name ?name]])") =>
        ((MinigrafQueryResult)tx.Execute(query)).Rows;

    private static MinigrafValue One(MinigrafPreparedDatabase db, string query) =>
        ((MinigrafQueryResult)db.Execute(query)).Rows[0][0];

    private static Guid EntityOf(MinigrafPreparedDatabase db) =>
        ((MinigrafQueryResult)db.Execute("(query [:find ?e :where [?e :name \"Alice\"]])"))
        .Rows[0][0].GetRef();

    private static string OfficialAssetPath()
    {
        string rid;
        string fileName;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            rid = "win-x64";
            fileName = "minigraf.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            rid = "osx-arm64";
            fileName = "libminigraf.dylib";
        }
        else
        {
            rid = "linux-x64";
            fileName = "libminigraf.so";
        }

        return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);
    }

    private static T Expect<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            Check(true, message);
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertEx($"{message}: expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}");
        }

        throw new AssertEx($"{message}: expected {typeof(T).Name}, no exception was thrown");
    }

    private static string FileFix(string name)
    {
        string dir = Path.Combine(RootTemp, Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        CleanupDirs.Add(dir);
        return Path.Combine(dir, name + ".graph");
    }

    private static void Case(string name, Action body)
    {
        try
        {
            body();
            _pass++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _fail++;
            FailingCases.Add(name);
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    private static void Cleanup()
    {
        List<string> undeletable = [];
        foreach (string dir in CleanupDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
                undeletable.Add(dir);
            }
        }

        Console.WriteLine("cleanup: complete=" + (undeletable.Count == 0 ? "true" : "false:" + string.Join(",", undeletable)));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertEx(message);
        }
    }

    private sealed class AssertEx(string message) : Exception(message);
}
