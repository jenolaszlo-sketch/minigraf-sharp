using MinigrafSharp;

internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> FailingCases = [];
    private static readonly List<string> CleanupDirs = [];

    private static string RootTemp { get; } =
        Path.Combine(Path.GetTempPath(), "MinigrafSharp.OpenOptionsQualificationTests");

    private static void Main()
    {
        Console.WriteLine("managed open-options qualification (M3.2c):");
        Console.WriteLine("  surface: MinigrafOpenOptions / MinigrafSyncMode on MinigrafPreparedDatabase");
        Console.WriteLine("  scope: host RID, net8.0 and net10.0");

        Cases();
        Console.WriteLine($"pass={_pass} fail={_fail}");
        Cleanup();
        if (_fail != 0)
        {
            Console.WriteLine("failed case: " + string.Join(", ", FailingCases));
            Environment.Exit(1);
        }

        Console.WriteLine("ALL OPEN-OPTIONS QUALIFICATION TESTS PASSED");
    }

    private static void Cases()
    {
        Case("default options preserve the existing open behavior", DefaultsPreserved);
        Case("wal threshold=1 checkpoints on each commit (WAL removed)", ThresholdOneCheckpoints);
        Case("wal threshold=Unlimited defers persistence to WAL replay", UnlimitedDefersCheckpoint);
        Case("max_results is enforced (INT-020)", MaxResultsEnforced);
        Case("max_derived_facts is enforced (INT-020)", MaxDerivedFactsEnforced);
        Case("synchronous Full and Normal are accepted", SynchronousAccepted);
        Case("invalid option values fail before any native call", InvalidOptionsRejected);
        Case("null and empty arguments are rejected", ArgumentValidation);
        Case("in-memory options accepts WAL options as no-ops", InMemoryOptions);
        Case("structured open failure carries native category/code and text", StructuredOpenFailure);
        Case("options open leaves plain default open unchanged", DefaultsUnaffectedByOptionsUse);
    }

    private static void DefaultsPreserved()
    {
        string path = FileFix("defaults");
        using (var db = MinigrafPreparedDatabase.Open(path, MinigrafOpenOptions.Default))
        {
            db.Execute("""(transact [[:a :name "A"]])""");
        }

        using var reopened = MinigrafPreparedDatabase.Open(path);
        Check(Rows(reopened).Count == 1, "options-default open persists like the existing open");
        CheckNoWalRemains(path);
    }

    private static void ThresholdOneCheckpoints()
    {
        string path = FileFix("threshold-one");
        using (var db = MinigrafPreparedDatabase.Open(path,
                   new MinigrafOpenOptions { WalCheckpointThreshold = 1 }))
        {
            db.Execute("""(transact [[:a :name "A"]])""");
            Check(!File.Exists(path + ".wal"),
                "threshold=1 auto-checkpoints each commit (WAL absent)");
        }
    }

    private static void UnlimitedDefersCheckpoint()
    {
        string path = FileFix("threshold-unlimited");
        var options = new MinigrafOpenOptions
        {
            WalCheckpointThreshold = MinigrafOpenOptions.Unlimited,
        };
        using (var db = MinigrafPreparedDatabase.Open(path, options))
        {
            db.Execute("""(transact [[:a :name "A"]])""");
        }

        Check(File.Exists(path + ".wal"),
            "Unlimited leaves the WAL in place after a clean close");
        using var reopened = MinigrafPreparedDatabase.Open(path);
        Check(Rows(reopened).Count == 1, "WAL replay restores the committed fact on reopen");
    }

    private static void MaxResultsEnforced()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory(
            new MinigrafOpenOptions { MaxResults = 1 });
        db.Execute("""(transact [[:a :edge :b] [:a :edge :c]])""");
        db.Execute("(rule [(reach ?x ?y) [?x :edge ?y]])");
        var error = Expect<MinigrafExecutionException>(
            () => db.Execute("(query [:find ?y :where (reach :a ?y)])"),
            "max_results=1 rejects a 2-row derivation");
        Check(error.Message.Contains("INT-020", StringComparison.Ordinal),
            "measured limit diagnostic INT-020: " + error.Message);
    }

    private static void MaxDerivedFactsEnforced()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory(
            new MinigrafOpenOptions { MaxDerivedFacts = 1 });
        db.Execute("""(transact [[:a :edge :b] [:b :edge :c]])""");
        db.Execute("(rule [(reach ?x ?y) [?x :edge ?z] (reach ?z ?y)])");
        var error = Expect<MinigrafExecutionException>(
            () => db.Execute("(query [:find ?y :where (reach :a ?y)])"),
            "max_derived_facts=1 rejects the recursive derivation");
        Check(error.Message.Contains("INT-020", StringComparison.Ordinal),
            "measured limit diagnostic INT-020: " + error.Message);
    }

    private static void SynchronousAccepted()
    {
        using (var db = MinigrafPreparedDatabase.OpenMemory(
                   new MinigrafOpenOptions { Synchronous = MinigrafSyncMode.Normal }))
        {
            db.Execute("""(transact [[:a :name "A"]])""");
            Check(Rows(db).Count == 1, "Normal accepted in memory");
        }

        string path = FileFix("sync-normal");
        using (var db = MinigrafPreparedDatabase.Open(path,
                   new MinigrafOpenOptions
                   {
                       Synchronous = MinigrafSyncMode.Normal,
                       WalCheckpointThreshold = 1,
                   }))
        {
            db.Execute("""(transact [[:a :name "A"]])""");
        }

        using var reopened = MinigrafPreparedDatabase.Open(path);
        Check(Rows(reopened).Count == 1, "Normal accepted for a file-backed database");
    }

    private static void InvalidOptionsRejected()
    {
        var invalid = new MinigrafOpenOptions { Synchronous = (MinigrafSyncMode)42 };
        Expect<ArgumentException>(
            () => MinigrafPreparedDatabase.OpenMemory(invalid),
            "undefined sync mode rejected before native use");
    }

    private static void ArgumentValidation()
    {
        Expect<ArgumentNullException>(
            () => MinigrafPreparedDatabase.OpenMemory(null!),
            "null options rejected");
        Expect<ArgumentNullException>(
            () => MinigrafPreparedDatabase.Open(null!, MinigrafOpenOptions.Default),
            "null path rejected");
        Expect<ArgumentException>(
            () => MinigrafPreparedDatabase.Open(string.Empty, MinigrafOpenOptions.Default),
            "empty path rejected");
    }

    private static void InMemoryOptions()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory(new MinigrafOpenOptions
        {
            WalCheckpointThreshold = 1,
            MaxResults = 10,
            Synchronous = MinigrafSyncMode.Normal,
        });
        db.Execute("""(transact [[:a :name "A"]])""");
        Check(Rows(db).Count == 1, "in-memory options accepted as no-ops");
    }

    private static void StructuredOpenFailure()
    {
        string missingDir = Path.Combine(RootTemp, Guid.NewGuid().ToString("N"), "nested");
        string path = Path.Combine(missingDir, "db.graph");
        var error = Expect<MinigrafOpenException>(
            () => MinigrafPreparedDatabase.Open(path, MinigrafOpenOptions.Default),
            "open failure raises MinigrafOpenException");
        Check(!string.IsNullOrWhiteSpace(error.Message), "the native/open diagnostic text is preserved");
        Check(error.NativeCategory != MinigrafNativeErrorCategory.Unknown ||
              error.NativeCode is not null,
            "structured information present when the engine reported it (" +
            error.NativeCategory + "/" + error.NativeCode + ")");
    }

    private static void DefaultsUnaffectedByOptionsUse()
    {
        string path = FileFix("after-options");
        using (var db = MinigrafPreparedDatabase.Open(path,
                   new MinigrafOpenOptions { MaxResults = 500 }))
        {
            db.Execute("""(transact [[:a :name "A"]])""");
        }

        using var plain = MinigrafPreparedDatabase.Open(path);
        Check(Rows(plain).Count == 1, "plain open still reads the database");
        CheckNoWalRemains(path);
    }

    // --- helpers ---------------------------------------------------------

    private static IReadOnlyList<MinigrafRow> Rows(MinigrafPreparedDatabase db) =>
        ((MinigrafQueryResult)db.Execute(
            "(query [:find ?name :where [?e :name ?name]])")).Rows;

    private static void CheckNoWalRemains(string path)
    {
        Check(!File.Exists(path + ".wal"), "default/checkpointing open leaves no WAL after clean close");
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
