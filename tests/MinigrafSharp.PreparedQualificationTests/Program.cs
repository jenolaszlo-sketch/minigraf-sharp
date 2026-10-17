// M3.1b qualification: managed prepared-query ownership, loader identity and
// behavioral parity over the qualified owned shim (M3.1a, commit 480471e).
// Case-order invariant: the refusal probe runs BEFORE any successful shim
// load in this process, because a verified load is cached for process
// lifetime. Failed loads never cache a handle, so a refused probe leaves no
// poisoned state and the process continues normally afterwards.
using System.Security.Cryptography;
using System.Text.Json;
using MinigrafSharp;
using MinigrafSharp.Native;

internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> FailingCases = new();
    private static readonly List<string> CleanupDirs = new();
    private static string RootTemp = "";

    private sealed class AssertEx(string m) : Exception(m);

    internal static void Main()
    {
        try
        {
            RootTemp = Path.Combine(Path.GetTempPath(), "MinigrafSharp.PreparedQualificationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootTemp);

            Case("gate", Gate);
            Case("shim identity pin matches staged provenance", IdentityPinMatchesStagedProvenance);
            Case("shim identity mismatch refused, process continues", IdentityRefusal);
            Case("open memory + dispose + double dispose", OpenDoubleDispose);
            Case("prepare -> execute (val binds, values compared)", PrepareExecute);
            Case("prepare -> multiple executes with different bindings", MultiExecuteFreshState);
            Case("all supported binding kinds", AllBindingKinds);
            Case("missing binding rejected", MissingBinding);
            Case("extra/unknown binding ignored", ExtraBindingIgnored);
            Case("wrong binding type rejected", WrongBindingType);
            Case(":as-of via Transaction equals raw :as-of", AsOfTransactionParity);
            Case(":valid-at literal vs Timestamp slot not reinterpreted", ValidAtLiteralVsTimestampSlot);
            Case("far-future Timestamp parity", FarFutureTimestampParity);
            Case("query disposal: use-after-dispose rejects, parent reusable", QueryDisposal);
            Case("database cascade disposal", CascadeDisposal);
            Case("double disposal (query/db, both orders)", DoubleDisposal);
            Case("use-after-dispose", UseAfterDispose);
            Case("prepare failure cleanup", PrepareFailureCleanup);
            Case("execution failure cleanup", ExecutionFailureCleanup);
            Case("concurrent calls against one database (gate respected)", ConcurrentOneDatabase);
            Case("different databases do not share a global gate", IndependentDatabasesNoSharedGate);
            Case("independent calls against two databases in parallel", TwoDatabasesParallel);
            Case("raw-vs-prepared parity (values and bytes)", RawVsPreparedParity);
            Case("managed binding prechecks", BindingsPrechecks);
            Case("file-backed reopen replays WAL for prepared queries", FileReopenReplay);
            Case("open-after-dispose rejected", OpenAfterDispose);
        }
        finally
        {
            Cleanup();
        }

        Console.WriteLine();
        Console.WriteLine($"pass={_pass} fail={_fail}");
        foreach (string f in FailingCases)
        {
            Console.WriteLine($"failed case: {f}");
        }
        if (_fail == 0)
        {
            Console.WriteLine($"ALL {_pass} PREPARED-QUALIFICATION TESTS PASSED");
        }
        else
        {
            Environment.Exit(1);
        }
    }

    private static void Case(string name, Action body)
    {
        try
        {
            body();
            _pass++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            _fail++;
            FailingCases.Add(name);
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    private static void Check(bool cond, string msg)
    {
        if (!cond)
        {
            throw new AssertEx(msg);
        }
    }

    private static bool Throws<TException>(Func<object?> action)
        where TException : Exception
    {
        try
        {
            _ = action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
        catch (Exception ex) when (ex is not AssertEx)
        {
            return false;
        }
    }

    private static string UpstreamJsonPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "native", "upstream.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new AssertEx("native/upstream.json not found from " + AppContext.BaseDirectory);
    }

    private static string IdentityJsonPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "native", "shim", "identity.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new AssertEx("identity.json not found from " + AppContext.BaseDirectory);
    }

    private static string FileFix(string name)
    {
        string dir = Path.Combine(RootTemp, Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        CleanupDirs.Add(dir);
        return Path.Combine(dir, name + ".graph");
    }

    private static void Cleanup()
    {
        List<string> undeletable = [];
        foreach (string dir in CleanupDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception) { undeletable.Add(dir); }
        }
        CheckFolders();
        Console.WriteLine("cleanup: complete=" + (undeletable.Count == 0 ? "true" : "false:" + string.Join(",", undeletable)));
    }

    private static void CheckFolders()
    {
        var guard = new DirectoryInfo(RootTemp);
        while (guard is not null)
        {
            if (string.Equals(guard.Name, "MinigrafSharp.PreparedQualificationTests", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            guard = guard.Parent;
        }

        throw new AssertEx("cleanup guard violation: RootTemp is not under a disposable root");
    }

    private static void Gate()
    {
        Console.WriteLine("managed prepared-query qualification target (M3.1b):");
        Console.WriteLine("  pinned identity token: " + ShimNativeLoader.PinnedIdentityToken);
        Console.WriteLine("  override variables: official=" + NativeLoader.OverrideVariable +
            " shim=" + ShimNativeLoader.OverrideVariable);
        Console.WriteLine("  qualified scope: all packaged RIDs of this runner-OS build, net8.0 and net10.0 managed surface");
        string rid = NativeLoader.GetRuntimeIdentifier();
        string officialName = NativeLoader.GetNativeFileName(rid);
        string officialAsset = LocateAsset(NativeLoader.GetCandidatePaths(
            rid, officialName), officialName);
        string shimAsset = LocateShimAsset(rid);
        Check(officialAsset.EndsWith(officialName, StringComparison.OrdinalIgnoreCase), "official asset located: " + officialAsset);
        Check(shimAsset.EndsWith(ShimNativeLoader.GetNativeFileName(rid), StringComparison.OrdinalIgnoreCase), "shim asset located: " + shimAsset);
        Console.WriteLine("  official asset: " + officialAsset);
        Console.WriteLine("  shim asset: " + shimAsset);
    }

    private static string LocateAsset(string[] candidates, string mustEndWith)
    {
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate) && candidate.EndsWith(mustEndWith, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new AssertEx("no staged asset among: " + string.Join("; ", candidates));
    }

    // Same candidate walk as the loader, without loading: reports only.
    private static string LocateShimAsset(string rid)
    {
        string fileName = ShimNativeLoader.GetNativeFileName(rid);
        string baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "runtimes", rid, "native", fileName),
            Path.Combine(baseDir, fileName),
        };
        string? dir = baseDir;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir, "native", "staging", rid, fileName));
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return LocateAsset([.. candidates], fileName);
    }

    private static void IdentityPinMatchesStagedProvenance()
    {
        var upstream = JsonDocument.Parse(File.ReadAllText(UpstreamJsonPath())).RootElement;
        var identity = JsonDocument.Parse(File.ReadAllText(IdentityJsonPath())).RootElement;
        string composed =
            "{\"shim_abi\":" + JsonSerializer.Serialize(identity.GetProperty("shim_abi").GetString()) +
            ",\"shim_source_revision\":" + identity.GetProperty("source_revision").GetInt64() +
            ",\"core_version\":" + JsonSerializer.Serialize(upstream.GetProperty("minigraf").GetProperty("version").GetString()) +
            ",\"core_commit\":" + JsonSerializer.Serialize(upstream.GetProperty("minigraf").GetProperty("commit").GetString()) + "}";
        Check(composed == ShimNativeLoader.PinnedIdentityToken,
            "pinned managed identity token diverged from staged provenance files:\n  composed " + composed +
            "\n  pinned   " + ShimNativeLoader.PinnedIdentityToken);
    }

    private static Dictionary<string, MinigrafBindValue> Binds(params (string Name, MinigrafBindValue Value)[] entries)
    {
        var d = new Dictionary<string, MinigrafBindValue>(StringComparer.Ordinal);
        foreach ((string name, MinigrafBindValue value) in entries)
        {
            d.Add(name, value);
        }

        return d;
    }

    private static MinigrafQueryResult ExpectQuery(MinigrafResult result, string where)
    {
        Check(result is MinigrafQueryResult, where + ": expected query result, got " + result.GetType().Name);
        return (MinigrafQueryResult)result;
    }

    private static MinigrafTransactionResult ExpectTransact(MinigrafResult result, string where)
    {
        Check(result is MinigrafTransactionResult, where + ": expected transact result, got " + result.GetType().Name);
        return (MinigrafTransactionResult)result;
    }

    private static bool SameRows(MinigrafQueryResult left, MinigrafQueryResult right)
    {
        return left.Variables.SequenceEqual(right.Variables, StringComparer.Ordinal)
            && left.Rows.Count == right.Rows.Count
            && left.Rows.Zip(right.Rows).All(pair =>
                pair.First.Count == pair.Second.Count
                && Enumerable.Range(0, pair.First.Count)
                    .All(i => pair.First[i].Equals(pair.Second[i])));
    }

    private static void SeedsTwoPeople(MinigrafPreparedDatabase db)
    {
        ExpectTransact(db.Execute("(transact [[:alice :name \"Alice\"] [:alice :age 30] [:bob :name \"Bob\"] [:bob :age 25]])"), "fixture tx");
    }

    private static Guid UuidOf(MinigrafPreparedDatabase db, string query)
    {
        var rows = ExpectQuery(db.Execute(query), "uuid probe");
        string text = rows.Rows[0][0].GetString();
        Check(Guid.TryParse(text, out Guid value), "row is not a canonical UUID: " + text);
        return value;
    }

    internal static Guid UpstreamKeywordUuid(string keyword)
    {
        byte[] oidNamespace =
        [
            0x6b, 0xa7, 0xb8, 0x12, 0x9d, 0xad, 0x11, 0xd1,
            0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8,
        ];
        byte[] digest = SHA1.HashData(oidNamespace.Concat(System.Text.Encoding.UTF8.GetBytes(keyword)).ToArray());
        byte[] uuid = digest.AsSpan(0, 16).ToArray();
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        string hex = Convert.ToHexString(uuid).ToLowerInvariant();
        return Guid.Parse(
            $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}");
    }

    private static void IdentityRefusal()
    {
        string rid = NativeLoader.GetRuntimeIdentifier();
        string officialName = NativeLoader.GetNativeFileName(rid);
        string officialAsset = LocateAsset(
            NativeLoader.GetCandidatePaths(rid, officialName),
            officialName);
        Check(File.Exists(officialAsset), "official asset staged for refusal probe: " + officialAsset);
        string? previous = Environment.GetEnvironmentVariable(ShimNativeLoader.OverrideVariable);
        try
        {
            // First shim use of the process: the override points at the
            // official binary. The loader must refuse it (no symbol or not
            // the pinned identity) with nothing cached afterwards.
            Environment.SetEnvironmentVariable(ShimNativeLoader.OverrideVariable, officialAsset);
            MinigrafNativeLoadException? refusal = null;
            try
            {
                _ = MinigrafPreparedDatabase.OpenMemory();
            }
            catch (MinigrafNativeLoadException ex)
            {
                refusal = ex;
            }

            Check(refusal is not null, "official binary under the shim import must be refused with MinigrafNativeLoadException");
            Console.WriteLine("  observed (refusal): " + refusal!.Message);
            Check(refusal.Message.Contains("mgs_shim_identity"), "refusal diagnostic must name the identity mechanism");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShimNativeLoader.OverrideVariable, previous);
        }

        // Failed loads never cache a handle: without the override the next
        // open resolves the real shim and the process continues normally.
        using var db = MinigrafPreparedDatabase.OpenMemory();
        ExpectTransact(db.Execute("(transact [[:alice :name \"Alice\"]])"), "post-refusal fixture");
    }

    private static void OpenDoubleDispose()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Dispose();
        db.Dispose();
    }

    private static void PrepareExecute()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        using var pq = db.Prepare("(query [:find ?name :where [?e :name ?name] [?e :age $age]])");
        Check(pq.SlotNames.Count == 1 && pq.SlotNames[0] == "age", "slot names: [" + string.Join(",", pq.SlotNames) + "]");

        var bob = ExpectQuery(pq.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(25L))))), "val 25");
        Check(bob.Rows.Count == 1 && bob.Rows[0][0].GetString() == "Bob", "val 25 -> Bob");
        Check(bob.Variables[0] == "?name", "variables keep the ? prefix in the managed decode: " + string.Join(",", bob.Variables));

        var alice = ExpectQuery(pq.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(30L))))), "val 30");
        Check(alice.Rows.Count == 1 && alice.Rows[0][0].GetString() == "Alice", "val 30 -> Alice");

        var empty = ExpectQuery(pq.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(31L))))), "val 31");
        Check(empty.Rows.Count == 0, "val 31 -> empty result (representative empty case)");
    }

    private static void MultiExecuteFreshState()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        using var pq = db.Prepare("(query [:find ?name :where [?e :name ?name] [?e :age $age]])");
        var before = ExpectQuery(pq.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(40L))))), "before");
        Check(before.Rows.Count == 0, "no carol yet");

        ExpectTransact(db.Execute("(transact [[:carol :name \"Carol\"] [:carol :age 40]])"), "second tx");

        var after = ExpectQuery(pq.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(40L))))), "after");
        Check(after.Rows.Count == 1 && after.Rows[0][0].GetString() == "Carol",
            "fresh engine state visible to the held prepared query: " + after.Rows.Count);
    }

    private static void AllBindingKinds()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        ulong tx1 = ExpectTransact(
            db.Execute("(transact [[:alice :name \"Alice\"] [:alice :age 30] [:alice :score 9.5] [:alice :liked true] [:alice :friend :bob]])"),
            "fixture tx 1").TransactionId;
        Guid aliceUuid = UuidOf(db, "(query [:find ?e :where [?e :name \"Alice\"]])");
        Guid bobUuid = UpstreamKeywordUuid(":bob");

        using var entityQ = db.Prepare("(query [:find ?n :where [$who :name ?n]])");
        var byEntity = ExpectQuery(entityQ.Execute(Binds(("who", MinigrafBindValue.Entity(aliceUuid)))), "entity bind");
        Check(byEntity.Rows.Count == 1 && byEntity.Rows[0][0].GetString() == "Alice", "Entity -> Alice");

        using var valStringQ = db.Prepare("(query [:find ?a :where [?e :name $nm] [?e :age ?a]])");
        var byString = ExpectQuery(valStringQ.Execute(Binds(("nm", MinigrafBindValue.Val(new MinigrafValue("Alice"))))), "val string");
        Check(byString.Rows.Count == 1 && byString.Rows[0][0].GetInt64() == 30, "val string -> age 30");

        using var valIntQ = db.Prepare("(query [:find ?n :where [?e :age $age] [?e :name ?n]])");
        var byInt = ExpectQuery(valIntQ.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(30L))))), "val int");
        Check(byInt.Rows.Count == 1 && byInt.Rows[0][0].GetString() == "Alice", "val int -> Alice");

        using var valFloatQ = db.Prepare("(query [:find ?n :where [?e :score $sc] [?e :name ?n]])");
        var byFloat = ExpectQuery(valFloatQ.Execute(Binds(("sc", MinigrafBindValue.Val(new MinigrafValue(9.5))))), "val float");
        Check(byFloat.Rows.Count == 1 && byFloat.Rows[0][0].GetString() == "Alice", "val float -> Alice");

        using var valBoolQ = db.Prepare("(query [:find ?n :where [?e :liked $li] [?e :name ?n]])");
        var byBool = ExpectQuery(valBoolQ.Execute(Binds(("li", MinigrafBindValue.Val(new MinigrafValue(true))))), "val bool");
        Check(byBool.Rows.Count == 1 && byBool.Rows[0][0].GetString() == "Alice", "val bool -> Alice");

        using var refQ = db.Prepare("(query [:find ?n :where [?e :friend $fr] [?e :name ?n]])");
        var byRef = ExpectQuery(refQ.Execute(Binds(("fr", MinigrafBindValue.ValRef(bobUuid)))), "ref bind");
        Check(byRef.Rows.Count == 1 && byRef.Rows[0][0].GetString() == "Alice", "ref -> Alice (friend :bob)");

        using var keywordQ = db.Prepare("(query [:find ?e :where [?e :status $kw]])");
        bool keywordAccepted;
        try
        {
            _ = keywordQ.Execute(Binds(("kw", MinigrafBindValue.ValKeyword(":sleeping"))));
            keywordAccepted = true;
        }
        catch (MinigrafExecutionException)
        {
            keywordAccepted = false;
        }

        Check(keywordAccepted, "keyword bind must not fail the engine (acceptance probe; no :status facts yet)");

        ExpectTransact(db.Execute("(transact [[:carol :status :active]])"), "keyword fixture tx");
        var kwHit = ExpectQuery(keywordQ.Execute(Binds(("kw", MinigrafBindValue.ValKeyword(":active")))), "keyword :active");
        Check(kwHit.Rows.Count == 1, "keyword :active matches");
        var kwMiss = ExpectQuery(keywordQ.Execute(Binds(("kw", MinigrafBindValue.ValKeyword(":sleeping")))), "keyword :sleeping");
        Check(kwMiss.Rows.Count == 0, "keyword :sleeping stays empty");

        using var asOfQ = db.Prepare("(query [:find ?n :as-of $t :where [?e :name ?n]])");
        var counterOne = ExpectQuery(asOfQ.Execute(Binds(("t", MinigrafBindValue.Transaction(1)))), "tx_count 1");
        var counterOneRaw = ExpectQuery(db.Execute("(query [:find ?n :as-of 1 :where [?e :name ?n]])"), "raw as-of 1");
        Check(SameRows(counterOne, counterOneRaw), "Transaction(1) rows equal raw ':as-of 1'");
        var txIdBound = ExpectQuery(asOfQ.Execute(Binds(("t", MinigrafBindValue.Transaction(tx1)))), "as-of tx id");
        var txIdRaw = ExpectQuery(db.Execute("(query [:find ?n :as-of " + tx1 + " :where [?e :name ?n]])"), "raw as-of tx id");
        Check(SameRows(txIdBound, txIdRaw), "Transaction(tx1) rows equal raw ':as-of <tx1>'");

        using var valNullQ = db.Prepare("(query [:find ?e :where [?e :unwritten $uz]])");
        var valNull = ExpectQuery(valNullQ.Execute(Binds(("uz", MinigrafBindValue.Val(MinigrafValue.Null)))), "val null");
        Check(valNull.Rows.Count == 0, "val null accepted as a bind kind (no :unwritten data)");

        using var timestampQ = db.Prepare("(query [:find ?n :valid-at $vt :where [?e :name ?n]])");
        var pastPrepared = ExpectQuery(timestampQ.Execute(Binds(("vt", MinigrafBindValue.Timestamp(1_000_000)))), "timestamp deep past");
        var pastRawIso = ExpectQuery(db.Execute("(query [:find ?n :valid-at \"1970-01-01T00:16:40Z\" :where [?e :name ?n]])"), "raw ISO 1970");
        Check(pastPrepared.Rows.Count == 0 && pastRawIso.Rows.Count == 0,
            "deep-past timestamp agrees with the equivalent raw ISO literal");

        using var anyTimeQ = db.Prepare("(query [:find ?n :valid-at $vt :where [?e :name ?n]])");
        var anyTime = ExpectQuery(anyTimeQ.Execute(Binds(("vt", MinigrafBindValue.AnyValidTime()))), "any_valid_time");
        Check(anyTime.Rows.Count >= 1, "any_valid_time sees facts: " + anyTime.Rows.Count);
    }

    private static void MissingBinding()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        using var pq = db.Prepare("(query [:find ?name :where [?e :name ?name] [?e :age $age]])");
        MinigrafExecutionException? error = null;
        try
        {
            _ = pq.Execute(Binds());
        }
        catch (MinigrafExecutionException ex)
        {
            error = ex;
        }

        Check(error is not null, "empty bindings must reject the missing slot deterministically");
        Console.WriteLine("  observed (missing slot): " + error!.Message);
    }

    private static void ExtraBindingIgnored()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        using var pq = db.Prepare("(query [:find ?name :where [?e :name ?name] [?e :age $age]])");
        var without = ExpectQuery(pq.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(30L))))), "without extra");
        var with = ExpectQuery(pq.Execute(Binds(
            ("age", MinigrafBindValue.Val(new MinigrafValue(30L))),
            ("not_a_slot", MinigrafBindValue.Val(new MinigrafValue("extra"))))), "with extra");
        Check(without.Rows.Count == with.Rows.Count && without.Rows[0][0].GetString() == with.Rows[0][0].GetString(),
            "extra binding changed the result");
    }

    private static void WrongBindingType()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        using var pq = db.Prepare("(query [:find ?name :where [?e :name ?name] [?e :age $age]])");
        MinigrafExecutionException? timestampError = null;
        try
        {
            _ = pq.Execute(Binds(("age", MinigrafBindValue.Timestamp(1_000_000))));
        }
        catch (MinigrafExecutionException ex)
        {
            timestampError = ex;
        }

        Check(timestampError is not null, "timestamp bind in a value position must reject");
        Console.WriteLine("  observed (wrong kind, timestamp in val position): " + timestampError!.Message);

        using var anyTimeQ = db.Prepare("(query [:find ?n :where [?e :name $nm]])");
        MinigrafExecutionException? anyTimeError = null;
        try
        {
            _ = anyTimeQ.Execute(Binds(("nm", MinigrafBindValue.AnyValidTime())));
        }
        catch (MinigrafExecutionException ex)
        {
            anyTimeError = ex;
        }

        Check(anyTimeError is not null, "any_valid_time bind in a value position must reject");
        Console.WriteLine("  observed (wrong kind, any_valid_time in val position): " + anyTimeError!.Message);
    }

    private static void AsOfTransactionParity()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        ulong id1 = ExpectTransact(db.Execute("(transact [[:alice :name \"Alice\"]])"), "tx 1").TransactionId;
        Thread.Sleep(3);
        ulong id2 = ExpectTransact(db.Execute("(transact [[:bob :name \"Bob\"]])"), "tx 2").TransactionId;
        Check(id2 > id1, "engine tx ids are wall-clock milliseconds; id1=" + id1 + " id2=" + id2);
        Console.WriteLine($"  observed (tx ids): id1={id1} id2={id2}");

        using var pq = db.Prepare("(query [:find ?name :as-of $t :where [?e :name ?name]])");
        var at1Prepared = ExpectQuery(pq.Execute(Binds(("t", MinigrafBindValue.Transaction(1)))), "Transaction(1)");
        var at1Raw = ExpectQuery(db.Execute("(query [:find ?name :as-of 1 :where [?e :name ?name]])"), "raw as-of 1");
        Check(SameRows(at1Prepared, at1Raw), "Transaction(1) must equal raw ':as-of 1'");

        var at2Prepared = ExpectQuery(pq.Execute(Binds(("t", MinigrafBindValue.Transaction(2)))), "Transaction(2)");
        var at2Raw = ExpectQuery(db.Execute("(query [:find ?name :as-of 2 :where [?e :name ?name]])"), "raw as-of 2");
        Check(SameRows(at2Prepared, at2Raw), "Transaction(2) must equal raw ':as-of 2'");
    }

    private static void ValidAtLiteralVsTimestampSlot()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        ExpectTransact(db.Execute("(transact [[:alice :name \"Alice\"] [:bob :name \"Bob\"]])"), "fixture");
        using var pq = db.Prepare("(query [:find ?name :valid-at $vt :where [?e :name ?name]])");

        var pastPrepared = ExpectQuery(pq.Execute(Binds(("vt", MinigrafBindValue.Timestamp(1_000_000)))), "timestamp 1000000");
        var pastRawIso = ExpectQuery(db.Execute("(query [:find ?name :valid-at \"1970-01-01T00:16:40Z\" :where [?e :name ?name]])"), "raw ISO 1970");
        Check(SameRows(pastPrepared, pastRawIso),
            "Timestamp slot substitution must not be reinterpreted relative to the raw ISO literal form");

        // The raw literal grammar is ISO-string-only; integer literals never
        // substitute instant positions (PRS-018), while Timestamp(i64) slots
        // bypass raw parsing. Measured, not invented.
        MinigrafExecutionException? rawIntError = null;
        try
        {
            _ = db.ExecuteRaw("(query [:find ?name :valid-at 1000000 :where [?e :name ?name]])");
        }
        catch (MinigrafExecutionException ex)
        {
            rawIntError = ex;
        }

        Check(rawIntError is not null, "raw integer :valid-at must reject (PRS-018)");
        Console.WriteLine("  observed (raw integer valid-at): " + rawIntError!.Message);
        var slotStillWorks = ExpectQuery(pq.Execute(Binds(("vt", MinigrafBindValue.Timestamp(1_000_000)))), "slot after raw rejection");
        Check(SameRows(slotStillWorks, pastRawIso), "Timestamp slot remains accepted where the raw integer literal is not");
    }

    private static void FarFutureTimestampParity()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        ExpectTransact(db.Execute("(transact [[:alice :name \"Alice\"]])"), "fixture");
        // 9999-12-31T23:59:59Z == 253402300799000 (M3.1a measured parity).
        using var pq = db.Prepare("(query [:find ?name :valid-at $vt :where [?e :name ?name]])");
        var farPrepared = ExpectQuery(pq.Execute(Binds(("vt", MinigrafBindValue.Timestamp(253402300799000)))), "far future slot");
        var farRaw = ExpectQuery(db.Execute("(query [:find ?name :valid-at \"9999-12-31T23:59:59Z\" :where [?e :name ?name]])"), "raw ISO 9999");
        Check(SameRows(farPrepared, farRaw), "far-future Timestamp slot must equal the raw ISO 9999 literal");
    }

    private static void QueryDisposal()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        var pq = db.Prepare("(query [:find ?name :where [?e :name ?name]])");
        _ = ExpectQuery(pq.Execute(Binds()), "pre-dispose execute");
        pq.Dispose();

        Check(Throws<ObjectDisposedException>(() => pq.Execute(Binds())),
            "execute after dispose must throw ObjectDisposedException");
        Check(pq.SlotNames.Count == 0, "slot names remain readable after dispose (captured state)");

        // Parent remains fully usable; re-preparing is allowed.
        using var pq2 = db.Prepare("(query [:find ?n :where [?e :name ?n]])");
        var again = ExpectQuery(pq2.Execute(Binds()), "re-prepare after child dispose");
        Check(again.Rows.Count == 2, "parent still answers after child dispose: " + again.Rows.Count);

        pq.Dispose();
    }

    private static void CascadeDisposal()
    {
        var db = MinigrafPreparedDatabase.OpenMemory();
        try
        {
            SeedsTwoPeople(db);
            var q1 = db.Prepare("(query [:find ?n :where [?e :name ?n]])");
            var q2 = db.Prepare("(query [:find ?a :where [?e :age ?a]])");
            db.Dispose();

            Check(Throws<ObjectDisposedException>(() => q1.Execute(Binds())), "child use after cascade must reject");
            Check(Throws<ObjectDisposedException>(() => q2.Execute(Binds())), "second child use after cascade must reject");
            q1.Dispose();
            q2.Dispose();
            db.Dispose();
        }
        finally { db.Dispose(); }
    }

    private static void DoubleDisposal()
    {
        var db = MinigrafPreparedDatabase.OpenMemory();
        try
        {
            SeedsTwoPeople(db);
            var q = db.Prepare("(query [:find ?n :where [?e :name ?n]])");
            q.Dispose();
            q.Dispose();
            db.Dispose();
            q.Dispose();
            db.Dispose();
        }
        finally { db.Dispose(); }
    }

    private static void UseAfterDispose()
    {
        var db = MinigrafPreparedDatabase.OpenMemory();
        try
        {
            SeedsTwoPeople(db);
            var q = db.Prepare("(query [:find ?n :where [?e :name ?n]])");
            q.Dispose();
            Check(Throws<ObjectDisposedException>(() => q.Execute(Binds())), "query use after its own dispose");
            db.Dispose();
            Check(Throws<ObjectDisposedException>(() => q.Execute(Binds())), "query use after parent dispose (cascade)");
        }
        finally { db.Dispose(); }
    }

    private static void PrepareFailureCleanup()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);

        Check(Throws<MinigrafExecutionException>(() => db.Prepare("(transact [[:alice :name \"Alice\"]])")), "transact prepare rejected");
        Check(Throws<MinigrafExecutionException>(() => db.Prepare("not valid datalog !!!")), "parse failure prepare rejected");
        Check(Throws<MinigrafExecutionException>(() => db.Prepare("(query [:find ?v :where [?e $attr ?v]])")), "attribute slot prepare rejected");
        Check(Throws<ArgumentException>(() => db.Prepare("")), "empty prepare rejected as managed argument error");
        Check(Throws<ArgumentNullException>(() => db.Prepare(null!)), "null prepare rejected as managed argument error");

        // The database stays fully usable after failed prepares.
        using var pq = db.Prepare("(query [:find ?n :where [?e :name ?n]])");
        var rows = ExpectQuery(pq.Execute(Binds()), "post-failure prepare");
        Check(rows.Rows.Count == 2, "db reusable after failed prepare: " + rows.Rows.Count);
    }

    private static void ExecutionFailureCleanup()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        using var pq = db.Prepare("(query [:find ?name :where [?e :name ?name] [?e :age $age]])");

        MinigrafExecutionException? error = null;
        try
        {
            _ = pq.Execute(Binds(("age", MinigrafBindValue.Timestamp(1_000_000))));
        }
        catch (MinigrafExecutionException ex)
        {
            error = ex;
        }

        Check(error is not null, "representative execution failure occurred");

        // After a failed execute, both the query and the database are usable.
        var again = ExpectQuery(pq.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(30L))))), "query usable after failure");
        Check(again.Rows.Count == 1 && again.Rows[0][0].GetString() == "Alice", "query state intact after failure");

        var dbAgain = ExpectQuery(db.Execute("(query [:find ?n :where [?e :name ?n]])"), "db usable after failure");
        Check(dbAgain.Rows.Count == 2, "db data intact after failure");

        using var fresh = db.Prepare("(query [:find ?a :where [?e :age ?a]])");
        var freshRows = ExpectQuery(fresh.Execute(Binds()), "prepare after failure");
        Check(freshRows.Rows.Count == 2, "prepare still possible after failure");
    }

    private static void ConcurrentOneDatabase()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        using var pq = db.Prepare("(query [:find ?name :where [?e :name ?name] [?e :age $age]])");
        var failures = new List<string>();
        Parallel.For(0, 4, worker =>
        {
            for (int i = 0; i < 25; i++)
            {
                try
                {
                    if (worker == 3 && i % 5 == 0)
                    {
                        // Interleave the raw write path against the same
                        // database: the per-database gate must serialize it
                        // with prepared reads, no corruption, no deadlock.
                        ExpectTransact(db.Execute("(transact [[:dave :name \"Dave\"] [:dave :age 50]])"), "concurrent write");
                    }
                    else
                    {
                        MinigrafBindValue bind = i % 2 == 0
                            ? MinigrafBindValue.Val(new MinigrafValue(30L))
                            : MinigrafBindValue.Val(new MinigrafValue(25L));
                        var rows = ExpectQuery(pq.Execute(Binds(("age", bind))), "concurrent read");
                        if (rows.Rows.Count != 1)
                        {
                            lock (failures) { failures.Add("row count " + rows.Rows.Count + " for worker " + worker); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (failures) { failures.Add(ex.GetType().Name + ": " + ex.Message); }
                }
            }
        });
        Check(failures.Count == 0, "concurrent calls on one database: " + string.Join("; ", failures.Take(3)));
    }

    private static void IndependentDatabasesNoSharedGate()
    {
        using var db1 = MinigrafPreparedDatabase.OpenMemory();
        using var db2 = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db1);
        SeedsTwoPeople(db2);

        // While db1's gate is deliberately held (no native call in flight),
        // db2 must still proceed: the gates are per-database and there is no
        // global managed serialization gate.
        lock (db1._sync)
        {
            string result = db2.ExecuteRaw("(query [:find ?n :where [?e :name ?n]])");
            Check(result.Length > 0, "db2 ran while db1's gate was held");
        }
    }

    private static void TwoDatabasesParallel()
    {
        using var db1 = MinigrafPreparedDatabase.OpenMemory();
        using var db2 = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db1);
        SeedsTwoPeople(db2);
        using var q1 = db1.Prepare("(query [:find ?n :where [?e :name ?n]])");
        using var q2 = db2.Prepare("(query [:find ?a :where [?e :age ?a]])");
        var failures = new List<string>();
        Parallel.For(0, 4, worker =>
        {
            (MinigrafPreparedQuery pq, string kind) = worker % 2 == 0 ? (q1, "name") : (q2, "age");
            for (int i = 0; i < 25; i++)
            {
                try
                {
                    var rows = ExpectQuery(pq.Execute(Binds()), "parallel db");
                    if (rows.Rows.Count != 2)
                    {
                        lock (failures) { failures.Add(kind + " rows " + rows.Rows.Count); }
                    }
                }
                catch (Exception ex)
                {
                    lock (failures) { failures.Add(ex.GetType().Name + ": " + ex.Message); }
                }
            }
        });
        Check(failures.Count == 0, "parallel independent databases: " + string.Join("; ", failures.Take(3)));
    }

    private static void RawVsPreparedParity()
    {
        using var official = MinigrafDatabase.OpenMemory();
        using var prepared = MinigrafPreparedDatabase.OpenMemory();
        const string fixture = "(transact [[:alice :name \"Alice\"] [:alice :age 30] [:bob :name \"Bob\"] [:bob :age 25]])";
        _ = official.ExecuteRaw(fixture);
        _ = prepared.ExecuteRaw(fixture);

        const string query = "(query [:find ?n :where [?e :name ?n]])";
        string officialRaw = official.ExecuteRaw(query);
        string preparedRaw = prepared.ExecuteRaw(query);
        Check(officialRaw == preparedRaw,
            "raw-vs-prepared result JSON diverged:\n official " + officialRaw + "\n prepared " + preparedRaw);

        // Decoded comparison (same decoder contract, typed values equal).
        var officialTyped = ExpectQuery(official.Execute(query), "official typed");
        var preparedTyped = ExpectQuery(prepared.Execute(query), "prepared typed");
        Check(SameRows(officialTyped, preparedTyped), "decoded prepared results must equal decoded official results");

        // Prepared-with-bindings vs the raw equivalent on the same database.
        using var pq = prepared.Prepare("(query [:find ?n :where [?e :name ?n] [?e :age $age]])");
        var bound = ExpectQuery(pq.Execute(Binds(("age", MinigrafBindValue.Val(new MinigrafValue(30L))))), "prepared with binding");
        var boundRaw = ExpectQuery(prepared.Execute("(query [:find ?n :where [?e :name ?n] [?e :age 30]])"), "raw equivalent");
        Check(SameRows(bound, boundRaw), "prepared-with-binding must equal the raw equivalent");
    }

    private static void BindingsPrechecks()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        SeedsTwoPeople(db);
        using var pq = db.Prepare("(query [:find ?name :where [?e :name ?name] [?e :age $age]])");

        Check(Throws<ArgumentNullException>(() => pq.Execute(null!)), "null bindings dictionary");
        // A null binding NAME is not reachable through Dictionary<string, T>;
        // Execute keeps a defensive check, but the reachable precheck here is
        // the empty-name form.
        Check(Throws<ArgumentException>(() => pq.Execute(Binds(("", MinigrafBindValue.Val(new MinigrafValue(30L)))))), "empty binding name");
        Check(Throws<ArgumentException>(() => MinigrafBindValue.Val(new MinigrafValue(double.NaN))), "NaN float Val");
        Check(Throws<ArgumentException>(() => MinigrafBindValue.Val(new MinigrafValue(double.PositiveInfinity))), "+Inf float Val");
        Check(Throws<ArgumentNullException>(() => MinigrafBindValue.ValKeyword(null!)), "null keyword");
        Check(Throws<ArgumentException>(() => MinigrafBindValue.ValKeyword("")), "empty keyword");

        Check(Throws<ArgumentNullException>(() => db.ExecuteRaw(null!)), "null ExecuteRaw");
        Check(Throws<ArgumentNullException>(() => db.Execute(null!)), "null Execute");
        // Empty datalog follows the M1a official-surface semantics: passed
        // as supplied to the engine, which rejects it; no managed precheck
        // was added (parity with MinigrafDatabase).
        Check(Throws<MinigrafExecutionException>(() => db.ExecuteRaw("")), "empty ExecuteRaw rejected by the engine");
    }

    private static void FileReopenReplay()
    {
        string path = FileFix("prepared_replay");
        {
            using var db = MinigrafPreparedDatabase.Open(path);
            ExpectTransact(db.Execute("(transact [[:alice :name \"Alice\"]])"), "file fixture tx");
            using var pq = db.Prepare("(query [:find ?n :where [?e :name ?n]])");
            var before = ExpectQuery(pq.Execute(Binds()), "pre-close");
            Check(before.Rows.Count == 1, "pre-close rows: " + before.Rows.Count);
        }

        using (var reopened = MinigrafPreparedDatabase.Open(path))
        {
            using var pq = reopened.Prepare("(query [:find ?n :where [?e :name ?n]])");
            var after = ExpectQuery(pq.Execute(Binds()), "post-reopen");
            Check(after.Rows.Count == 1 && after.Rows[0][0].GetString() == "Alice",
                "WAL replay must be visible to a prepared query after reopen: " + after.Rows.Count);
        }

        // The official library reads the same file afterwards (format v7).
        using (var official = MinigrafDatabase.Open(path))
        {
            var officialRows = ExpectQuery(official.Execute("(query [:find ?n :where [?e :name ?n]])"), "official reader");
            Check(officialRows.Rows.Count == 1, "official reader opens the prepared-written file: " + officialRows.Rows.Count);
        }
    }

    private static void OpenAfterDispose()
    {
        var db = MinigrafPreparedDatabase.OpenMemory();
        db.Dispose();
        Check(Throws<ObjectDisposedException>(() => db.Prepare("(query [:find ?n :where [?e :name ?n]])")), "prepare after dispose");
        Check(Throws<ObjectDisposedException>(() => db.ExecuteRaw("(query [:find ?n :where [?e :name ?n]])")), "ExecuteRaw after dispose");
        Check(Throws<ObjectDisposedException>(() => db.Execute("(query [:find ?n :where [?e :name ?n]])")), "Execute after dispose");
        db.Dispose();
    }
}
