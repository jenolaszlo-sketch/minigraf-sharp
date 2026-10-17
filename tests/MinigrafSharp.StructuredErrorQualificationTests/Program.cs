using MinigrafSharp;
using MinigrafSharp.Interop;

internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> FailingCases = [];
    private static readonly List<string> CleanupDirs = [];

    private static string RootTemp { get; } =
        Path.Combine(Path.GetTempPath(), "MinigrafSharp.StructuredErrorQualificationTests");

    private static void Main()
    {
        Console.WriteLine("structured-error qualification (M3.2d):");
        Console.WriteLine("  surface: MinigrafException.NativeCategory/NativeCode over the owned shim");
        Console.WriteLine("  scope: host RID, net8.0 and net10.0");

        Cases();
        Console.WriteLine($"pass={_pass} fail={_fail}");
        Cleanup();
        if (_fail != 0)
        {
            Console.WriteLine("failed case: " + string.Join(", ", FailingCases));
            Environment.Exit(1);
        }

        Console.WriteLine("ALL STRUCTURED-ERROR QUALIFICATION TESTS PASSED");
    }

    private static void Cases()
    {
        Case("parser failure (PRS-*)", ParserFailure);
        Case("API failure (API-*)", ApiFailure);
        Case("query failure (QRY-*)", QueryFailure);
        Case("limit failure (INT-020)", LimitFailure);
        Case("reentrancy failure (INT-001)", ReentrancyFailure);
        Case("commit failure (INT-000) retains diagnostics", CommitFailure);
        Case("shim classification (MGS-001/MGS-003 payloads)", ShimClassification);
        Case("prepared bind failure (INT-033)", PreparedMissingBinding);
        Case("prepared bind-type failure (INT-035)", PreparedWrongBindingType);
        Case("prepared execution failure (QRY-007)", PreparedExecutionFailure);
        Case("open failure is structured", OpenFailureStructured);
        Case("later failure replaces an earlier error", FailureReplacement);
        Case("success clears the previous error", StaleClearedBySuccess);
        Case("absent structured metadata falls back to Unknown/null", AbsentMetadata);
        Case("malformed metadata falls back to Unknown/null", MalformedMetadata);
        Case("unknown category drops the code", UnknownCategory);
        Case("missing code yields a known category with null code", MissingCode);
        Case("code-looking text is never parsed for classification", NoMessageParsing);
        Case("exception hierarchy is unchanged and additive fields are present", ExceptionCompatibility);
    }

    private static void ParserFailure()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var error = Expect<MinigrafExecutionException>(
            () => db.Execute("(query [:find ?x :where [?x :a ?v]"),
            "malformed query fails");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Parser,
            "category Parser, got " + error.NativeCategory);
        Check(error.NativeCode is not null && error.NativeCode.StartsWith("PRS-", StringComparison.Ordinal),
            "code PRS-*, got " + error.NativeCode);
        Check(error.Message.Contains(error.NativeCode!, StringComparison.Ordinal),
            "message preserves the native diagnostic: " + error.Message);
    }

    private static void ApiFailure()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var error = Expect<MinigrafExecutionException>(
            () => db.Execute("""(transact [[:e "not-a-keyword" 1]])"""),
            "non-keyword attribute fails");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Api,
            "category Api, got " + error.NativeCategory);
        Check(error.NativeCode == "API-003", "measured API-003, got " + error.NativeCode);
    }

    private static void QueryFailure()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute("""(transact [[:a :x "hello"]])""");
        var error = Expect<MinigrafExecutionException>(
            () => db.Execute("(query [:find ?e :where [?e :x ?v] [(nosuchpred? ?v)]])"),
            "unknown predicate fails at query time");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Query,
            "category Query, got " + error.NativeCategory);
        Check(error.NativeCode == "QRY-007", "measured QRY-007, got " + error.NativeCode);
    }

    private static void LimitFailure()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory(
            new MinigrafOpenOptions { MaxResults = 1 });
        db.Execute("""(transact [[:a :edge :b] [:a :edge :c]])""");
        db.Execute("(rule [(reach ?x ?y) [?x :edge ?y]])");
        var error = Expect<MinigrafExecutionException>(
            () => db.Execute("(query [:find ?y :where (reach :a ?y)])"),
            "limit enforcement fails");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Internal,
            "category Internal, got " + error.NativeCategory);
        Check(error.NativeCode == "INT-020", "measured INT-020, got " + error.NativeCode);
    }

    private static void ReentrancyFailure()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        var error = Expect<MinigrafExecutionException>(
            () => db.Execute("""(transact [[:b :v 2]])"""),
            "implicit execute on the transaction-owning thread fails");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Internal,
            "category Internal, got " + error.NativeCategory);
        Check(error.NativeCode == "INT-001", "measured INT-001, got " + error.NativeCode);
        tx.Rollback();
    }

    private static void CommitFailure()
    {
        string path = FileFix("commit");
        using var db = MinigrafPreparedDatabase.Open(path);
        string wal = path + ".wal";
        _ = Directory.CreateDirectory(wal);
        try
        {
            var tx = db.BeginWrite();
            tx.Execute("""(transact [[:a :v 1]])""");
            var error = Expect<MinigrafExecutionException>(tx.Commit, "commit fails");
            Check(error.NativeCategory == MinigrafNativeErrorCategory.Internal,
                "category Internal, got " + error.NativeCategory);
            Check(error.NativeCode == "INT-000", "measured INT-000, got " + error.NativeCode);
            Check(!string.IsNullOrWhiteSpace(error.Message), "diagnostic preserved: " + error.Message);
            tx.Dispose();
        }
        finally
        {
            Directory.Delete(wal);
        }
    }

    private static void ShimClassification()
    {
        // The shim-owned codes are exercised at ABI level in the shim harness;
        // here the managed parser's mapping of the wire shape is qualified.
        var m1 = NativeErrorJson.Parse(
            """{"category":"Shim","code":"MGS-001","message":"thread-affine"}""");
        Check(m1 is { Category: MinigrafNativeErrorCategory.Shim, Code: "MGS-001" },
            "MGS-001 maps to Shim: " + m1);
        var m3 = NativeErrorJson.Parse(
            """{"category":"Shim","code":"MGS-003","message":"invalid schema"}""");
        Check(m3 is { Category: MinigrafNativeErrorCategory.Shim, Code: "MGS-003" },
            "MGS-003 maps to Shim: " + m3);
    }

    private static void PreparedMissingBinding()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var query = db.Prepare("(query [:find ?e :where [?e :name $name]])");
        var error = Expect<MinigrafExecutionException>(
            () => query.Execute(new Dictionary<string, MinigrafBindValue>()),
            "missing binding fails");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Internal,
            "category Internal, got " + error.NativeCategory);
        Check(error.NativeCode == "INT-033", "measured INT-033, got " + error.NativeCode);
    }

    private static void PreparedWrongBindingType()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var query = db.Prepare(
            "(query [:find ?e :where [?e :name ?n] [(>= $age 1)]])");
        var error = Expect<MinigrafExecutionException>(
            () => query.Execute(new Dictionary<string, MinigrafBindValue>
            {
                ["age"] = MinigrafBindValue.Timestamp(1),
            }),
            "wrong binding type fails");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Internal,
            "category Internal, got " + error.NativeCategory);
        Check(error.NativeCode == "INT-035", "measured INT-035, got " + error.NativeCode);
    }

    private static void PreparedExecutionFailure()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute("""(transact [[:a :x "hello"]])""");
        using var query = db.Prepare(
            "(query [:find ?e :where [?e :x ?v] [(nosuchpred? ?v)]])");
        var error = Expect<MinigrafExecutionException>(
            () => query.Execute(new Dictionary<string, MinigrafBindValue>()),
            "prepared execution fails");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Query,
            "category Query, got " + error.NativeCategory);
        Check(error.NativeCode == "QRY-007", "measured QRY-007, got " + error.NativeCode);
    }

    private static void OpenFailureStructured()
    {
        string path = Path.Combine(RootTemp, Guid.NewGuid().ToString("N"), "nested", "db.graph");
        var error = Expect<MinigrafOpenException>(
            () => MinigrafPreparedDatabase.Open(path, MinigrafOpenOptions.Default),
            "options open fails");
        Check(error.NativeCategory != MinigrafNativeErrorCategory.Unknown,
            "open failure has a known category, got " + error.NativeCategory);
        Check(error.NativeCode is not null, "open failure has a code, got " + error.NativeCode);
        Console.WriteLine(
            $"  observed open failure: {error.NativeCategory}/{error.NativeCode} :: {error.Message}");
    }

    private static void FailureReplacement()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var first = Expect<MinigrafExecutionException>(
            () => db.Execute("(query [:find ?x :where [?x :a ?v]"),
            "first failure");
        Check(first.NativeCategory == MinigrafNativeErrorCategory.Parser, "first is Parser");
        var second = Expect<MinigrafExecutionException>(
            () => db.Execute("""(transact [[:e "not-a-keyword" 1]])"""),
            "second failure");
        Check(second.NativeCategory == MinigrafNativeErrorCategory.Api &&
              second.NativeCode == "API-003",
            "second failure replaced the first: " + second.NativeCategory + "/" + second.NativeCode);
    }

    private static void StaleClearedBySuccess()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        _ = Expect<MinigrafExecutionException>(
            () => db.Execute("(query [:find ?x :where [?x :a ?v]"),
            "failure before success");
        db.Execute("""(transact [[:a :v 1]])""");
        var after = Expect<MinigrafExecutionException>(
            () => db.Execute("""(transact [[:e "not-a-keyword" 1]])"""),
            "failure after success");
        Check(after.NativeCategory == MinigrafNativeErrorCategory.Api &&
              after.NativeCode == "API-003",
            "classification reflects the new failure, not the stale one: " +
            after.NativeCategory + "/" + after.NativeCode);
    }

    private static void AbsentMetadata()
    {
        // Classic open has no structured channel (no handle on failure): the
        // failure must degrade to Unknown/null and keep its message.
        string path = Path.Combine(RootTemp, Guid.NewGuid().ToString("N"), "nested", "db.graph");
        var error = Expect<MinigrafOpenException>(
            () => MinigrafPreparedDatabase.Open(path),
            "classic open failure");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Unknown,
            "absent metadata -> Unknown, got " + error.NativeCategory);
        Check(error.NativeCode is null, "absent metadata -> null code, got " + error.NativeCode);
        Check(!string.IsNullOrWhiteSpace(error.Message), "message preserved: " + error.Message);
    }

    private static void MalformedMetadata()
    {
        foreach (string payload in new[]
                 {
                     "not-json",
                     "{}",
                     """{"category":123,"code":"INT-001","message":"x"}""",
                     """{"category":"Internal","code":"INT-001"}""",
                     """{"category":"Internal","code":"INT-001","message":42}""",
                 })
        {
            var info = NativeErrorJson.Parse(payload);
            Check(info is { Category: MinigrafNativeErrorCategory.Unknown, Code: null },
                $"malformed payload falls back to Unknown/null: {payload} -> {info}");
            Check(info is not null && !string.IsNullOrWhiteSpace(info.Value.Message),
                "diagnostic text preserved for malformed payload: " + payload);
        }

        Check(NativeErrorJson.Parse(null) is null, "absent text parses to no metadata");
    }

    private static void UnknownCategory()
    {
        var info = NativeErrorJson.Parse(
            """{"category":"FutureCategory","code":"XYZ-999","message":"future"}""");
        Check(info is { Category: MinigrafNativeErrorCategory.Unknown, Code: null },
            "unknown category drops the code: " + info);
        Check(info is not null && info.Value.Message == "future", "message preserved");
    }

    private static void MissingCode()
    {
        var info = NativeErrorJson.Parse("""{"category":"Internal","message":"no code"}""");
        Check(info is { Category: MinigrafNativeErrorCategory.Internal, Code: null },
            "known category with missing code keeps the category and null code: " + info);
        var invalid = NativeErrorJson.Parse(
            """{"category":"Parser","code":"not-a-code","message":"bad shape"}""");
        Check(invalid is { Category: MinigrafNativeErrorCategory.Parser, Code: null },
            "structurally invalid code is dropped, category preserved: " + invalid);
    }

    private static void NoMessageParsing()
    {
        var info = NativeErrorJson.Parse(
            """{"category":"Internal","message":"[INT-020] Max query results exceeded"}""");
        Check(info is { Category: MinigrafNativeErrorCategory.Internal, Code: null },
            "a code-looking message without a code never yields NativeCode: " + info);
        Check(NativeErrorJson.Parse(null) is null,
            "no metadata means no classification even if callers hold code-looking text");
    }

    private static void ExceptionCompatibility()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        Exception failure = Expect<MinigrafExecutionException>(
            () => db.Execute("(query [:find ?x :where [?x :a ?v]"),
            "execution failure type");
        Check(failure is MinigrafException, "derives from the existing MinigrafException hierarchy");
        Check(failure is not (MinigrafOpenException or MinigrafNativeLoadException or MinigrafResultException),
            "execution failure keeps its existing type");
        Check(failure is MinigrafExecutionException, "catch blocks for MinigrafExecutionException keep working");
    }

    // --- helpers ---------------------------------------------------------

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
