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
        Path.Combine(Path.GetTempPath(), "MinigrafSharp.TypedValueQualificationTests");

    private static void Main()
    {
        Console.WriteLine("typed Ref/Keyword qualification (M3.2e):");
        Console.WriteLine("  surface: MinigrafPreparedDatabase, its prepared and transaction paths");
        Console.WriteLine("  scope: host RID, net8.0 and net10.0");

        Cases();
        Console.WriteLine($"pass={_pass} fail={_fail}");
        Cleanup();
        if (_fail != 0)
        {
            Console.WriteLine("failed case: " + string.Join(", ", FailingCases));
            Environment.Exit(1);
        }

        Console.WriteLine("ALL TYPED-VALUE QUALIFICATION TESTS PASSED");
    }

    private static void Cases()
    {
        Case("ref decodes typed with UUID identity", RefDecode);
        Case("keyword decodes typed with exact case-sensitive text", KeywordDecode);
        Case("prepared query results are typed", PreparedResultsTyped);
        Case("transaction results are typed", TransactionResultsTyped);
        Case("round trip: storage -> typed decode -> ValRef/ValKeyword bind -> typed result", BindingRoundTrip);
        Case("official MinigrafDatabase keeps flattened strings", OfficialFlatteningPreserved);
        Case("non-typed values keep byte-identical serialization", ScalarByteParity);
        Case("typed values intentionally diverge from official C serialization", TypedDivergence);
        Case("ordinary strings resembling typed values stay String", StringsAreNotInferred);
        Case("malformed UUID envelope is rejected", MalformedUuid);
        Case("missing/extra/non-string envelope fields are rejected", MalformedEnvelope);
        Case("unknown typed kind is rejected", UnknownTypedKind);
        Case("invalid keyword representations are rejected", InvalidKeyword);
        Case("accessors are kind-sensitive", KindSensitiveAccessors);
        Case("equality, hash and formatting distinguish typed values", EqualityAndFormatting);
    }

    private static void RefDecode()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute($$"""(transact [[:alice :friend #uuid "{{BobUuidText}}"]])""");
        var cell = One(db, "(query [:find ?v :where [:alice :friend ?v]])");
        Check(cell.Kind == MinigrafValueKind.Ref, "kind Ref, got " + cell.Kind);
        Check(cell.GetRef() == BobUuid, "UUID identity preserved: " + cell.GetRef());
        Check(Throws<InvalidOperationException>(() => cell.GetString()), "GetString rejects Ref");
    }

    private static void KeywordDecode()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute("""(transact [[:carol :status :Status/Active]])""");
        var cell = One(db, "(query [:find ?v :where [:carol :status ?v]])");
        Check(cell.Kind == MinigrafValueKind.Keyword, "kind Keyword, got " + cell.Kind);
        Check(cell.GetKeyword() == ":Status/Active", "exact text, no normalization: " + cell.GetKeyword());
        Check(Throws<InvalidOperationException>(() => cell.GetString()), "GetString rejects Keyword");
    }

    private static void PreparedResultsTyped()
    {
        using var db = Seeded();
        using var query = db.Prepare("(query [:find ?friend ?status :where [:alice :friend ?friend] [:alice :status ?status]])");
        var result = (MinigrafQueryResult)query.Execute(new Dictionary<string, MinigrafBindValue>());
        MinigrafRow row = result.Rows[0];
        Check(row[0].Kind == MinigrafValueKind.Ref && row[0].GetRef() == BobUuid,
            "prepared ref cell typed: " + row[0]);
        Check(row[1].Kind == MinigrafValueKind.Keyword && row[1].GetKeyword() == ":Status/Active",
            "prepared keyword cell typed: " + row[1]);
    }

    private static void TransactionResultsTyped()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var tx = db.BeginWrite();
        tx.Execute($$"""(transact [[:alice :friend #uuid "{{BobUuidText}}"] [:alice :status :active]])""");
        var result = (MinigrafQueryResult)tx.Execute(
            "(query [:find ?friend ?status :where [:alice :friend ?friend] [:alice :status ?status]])");
        MinigrafRow row = result.Rows[0];
        Check(row[0].Kind == MinigrafValueKind.Ref && row[0].GetRef() == BobUuid,
            "transaction ref cell typed: " + row[0]);
        Check(row[1].Kind == MinigrafValueKind.Keyword && row[1].GetKeyword() == ":active",
            "transaction keyword cell typed: " + row[1]);
        tx.Commit();
    }

    private static void BindingRoundTrip()
    {
        using var db = Seeded();
        // Existing typed bind kinds feed the prepared path; results stay typed.
        using var byRef = db.Prepare("(query [:find ?n :where [?e :friend $fr] [?e :name ?n]])");
        var refRows = (MinigrafQueryResult)byRef.Execute(new Dictionary<string, MinigrafBindValue>
        {
            ["fr"] = MinigrafBindValue.ValRef(BobUuid),
        });
        Check(refRows.Rows.Count == 1 && refRows.Rows[0]["?n"].GetString() == "Alice",
            "ValRef binding selects the friend fact");

        using var byKeyword = db.Prepare("(query [:find ?e :where [?e :status $kw]])");
        var kwRows = (MinigrafQueryResult)byKeyword.Execute(new Dictionary<string, MinigrafBindValue>
        {
            ["kw"] = MinigrafBindValue.ValKeyword(":Status/Active"),
        });
        Check(kwRows.Rows.Count == 1, "ValKeyword binding matches the stored keyword");
        Check(kwRows.Rows[0]["?e"].Kind == MinigrafValueKind.Ref,
            "entity column decodes typed Ref: " + kwRows.Rows[0]["?e"].Kind);
        var miss = (MinigrafQueryResult)byKeyword.Execute(new Dictionary<string, MinigrafBindValue>
        {
            ["kw"] = MinigrafBindValue.ValKeyword(":status/active"),
        });
        Check(miss.Rows.Count == 0, "keyword matching stays case-sensitive");

        // Type identity survives a second write through the decoded value path.
        using var byEntity = db.Prepare("(query [:find ?v :where [$who :friend ?v]])");
        var entityRows = (MinigrafQueryResult)byEntity.Execute(new Dictionary<string, MinigrafBindValue>
        {
            ["who"] = MinigrafBindValue.Entity(EntityOf(db)),
        });
        Check(entityRows.Rows.Count == 1 && entityRows.Rows[0][0].GetRef() == BobUuid,
            "Entity binding + typed Ref result round trip");
    }

    private static void OfficialFlatteningPreserved()
    {
        using var official = MinigrafDatabase.OpenMemory();
        official.Execute($$"""(transact [[:alice :friend #uuid "{{BobUuidText}}"] [:alice :status :Status/Active]])""");
        var friend = (MinigrafQueryResult)official.Execute("(query [:find ?v :where [:alice :friend ?v]])");
        Check(friend.Rows[0][0].Kind == MinigrafValueKind.String,
            "official ref remains String, got " + friend.Rows[0][0].Kind);
        Check(friend.Rows[0][0].GetString() == BobUuidText, "official ref string value preserved");
        var status = (MinigrafQueryResult)official.Execute("(query [:find ?v :where [:alice :status ?v]])");
        Check(status.Rows[0][0].Kind == MinigrafValueKind.String,
            "official keyword remains String, got " + status.Rows[0][0].Kind);
        Check(status.Rows[0][0].GetString() == ":Status/Active", "official keyword string preserved");
    }

    private static void ScalarByteParity()
    {
        using var official = MinigrafDatabase.OpenMemory();
        using var shim = MinigrafPreparedDatabase.OpenMemory();
        const string fixture =
            "(transact [[:e :s \"text\"] [:e :i 42] [:e :f 1.5] [:e :b true]])";
        _ = official.ExecuteRaw(fixture);
        _ = shim.ExecuteRaw(fixture);
        foreach (string query in new[]
                 {
                     "(query [:find ?v :where [:e :s ?v]])",
                     "(query [:find ?v :where [:e :i ?v]])",
                     "(query [:find ?v :where [:e :f ?v]])",
                     "(query [:find ?v :where [:e :b ?v]])",
                 })
        {
            string officialJson = official.ExecuteRaw(query);
            string shimJson = shim.ExecuteRaw(query);
            Check(officialJson == shimJson,
                "scalar byte parity for " + query + ":\n official " + officialJson + "\n shim     " + shimJson);
        }
    }

    private static void TypedDivergence()
    {
        using var official = MinigrafDatabase.OpenMemory();
        using var shim = MinigrafPreparedDatabase.OpenMemory();
        const string fixture =
            "(transact [[:alice :friend #uuid \"550e8400-e29b-41d4-a716-446655440001\"] [:alice :status :Status/Active]])";
        _ = official.ExecuteRaw(fixture);
        _ = shim.ExecuteRaw(fixture);

        string officialJson = official.ExecuteRaw("(query [:find ?v :where [:alice :friend ?v]])");
        string shimJson = shim.ExecuteRaw("(query [:find ?v :where [:alice :friend ?v]])");
        Check(shimJson.Contains("{\"type\":\"ref\",\"value\":\"" + BobUuidText + "\"}", StringComparison.Ordinal),
            "shim emits a typed ref envelope: " + shimJson);
        Check(officialJson.Contains("\"" + BobUuidText + "\"", StringComparison.Ordinal) &&
              !officialJson.Contains("\"type\"", StringComparison.Ordinal),
            "official keeps the flattened string: " + officialJson);

        string officialKw = official.ExecuteRaw("(query [:find ?v :where [:alice :status ?v]])");
        string shimKw = shim.ExecuteRaw("(query [:find ?v :where [:alice :status ?v]])");
        Check(shimKw.Contains("{\"type\":\"keyword\",\"value\":\":Status/Active\"}", StringComparison.Ordinal),
            "shim emits a typed keyword envelope: " + shimKw);
        Check(officialKw.Contains("\":Status/Active\"", StringComparison.Ordinal) &&
              !officialKw.Contains("\"type\"", StringComparison.Ordinal),
            "official keeps the flattened keyword string: " + officialKw);
    }

    private static void StringsAreNotInferred()
    {
        var uuidLike = DecodeSingle("\"" + BobUuidText + "\"");
        Check(uuidLike.Kind == MinigrafValueKind.String && uuidLike.GetString() == BobUuidText,
            "UUID-like ordinary string stays String: " + uuidLike);
        var keywordLike = DecodeSingle("\":Status/Active\"");
        Check(keywordLike.Kind == MinigrafValueKind.String && keywordLike.GetString() == ":Status/Active",
            "keyword-like ordinary string stays String: " + keywordLike);
    }

    private static void MalformedUuid()
    {
        foreach (string payload in new[]
                 {
                     "{\"type\":\"ref\",\"value\":\"not-a-uuid\"}",
                     "{\"type\":\"ref\",\"value\":\"550e8400e29b41d4a716446655440001\"}",
                     "{\"type\":\"ref\",\"value\":\"550e8400-e29b-41d4-a716-44665544000Z\"}",
                 })
        {
            Check(Throws<MinigrafResultException>(() => DecodeSingle(payload)),
                "malformed UUID rejected: " + payload);
        }
    }

    private static void MalformedEnvelope()
    {
        foreach (string payload in new[]
                 {
                     "{}",
                     "{\"type\":\"ref\"}",
                     "{\"value\":\"" + BobUuidText + "\"}",
                     "{\"type\":\"ref\",\"value\":\"" + BobUuidText + "\",\"extra\":1}",
                     "{\"type\":\"ref\",\"value\":42}",
                     "{\"type\":42,\"value\":\"" + BobUuidText + "\"}",
                 })
        {
            Check(Throws<MinigrafResultException>(() => DecodeSingle(payload)),
                "malformed envelope rejected: " + payload);
        }
    }

    private static void UnknownTypedKind()
    {
        Check(Throws<MinigrafResultException>(
                () => DecodeSingle("{\"type\":\"entity\",\"value\":\"" + BobUuidText + "\"}")),
            "unknown typed kind rejected");
    }

    private static void InvalidKeyword()
    {
        foreach (string payload in new[]
                 {
                     "{\"type\":\"keyword\",\"value\":\"status/active\"}",
                     "{\"type\":\"keyword\",\"value\":\":\"}",
                     "{\"type\":\"keyword\",\"value\":\"\"}",
                 })
        {
            Check(Throws<MinigrafResultException>(() => DecodeSingle(payload)),
                "invalid keyword rejected: " + payload);
        }
    }

    private static void KindSensitiveAccessors()
    {
        var keyword = DecodeSingle("{\"type\":\"keyword\",\"value\":\":k\"}");
        Check(Throws<InvalidOperationException>(() => keyword.GetRef()), "GetRef rejects Keyword");
        var reference = DecodeSingle("{\"type\":\"ref\",\"value\":\"" + BobUuidText + "\"}");
        Check(Throws<InvalidOperationException>(() => reference.GetKeyword()), "GetKeyword rejects Ref");
        Check(Throws<InvalidOperationException>(() => reference.GetInt64()), "GetInt64 rejects Ref");
    }

    private static void EqualityAndFormatting()
    {
        var refA = DecodeSingle("{\"type\":\"ref\",\"value\":\"" + BobUuidText + "\"}");
        var refB = DecodeSingle("{\"type\":\"ref\",\"value\":\"" + BobUuidText + "\"}");
        var refC = DecodeSingle("{\"type\":\"ref\",\"value\":\"00000000-0000-0000-0000-000000000000\"}");
        Check(refA.Equals(refB) && refA == refB && refA.GetHashCode() == refB.GetHashCode(),
            "equal refs compare and hash equal");
        Check(refA != refC, "different refs differ");
        Check(refA.ToString() == "Ref(" + BobUuidText + ")", "ref formatting: " + refA);

        var kwA = DecodeSingle("{\"type\":\"keyword\",\"value\":\":Status/Active\"}");
        var kwB = DecodeSingle("{\"type\":\"keyword\",\"value\":\":Status/Active\"}");
        var kwC = DecodeSingle("{\"type\":\"keyword\",\"value\":\":status/active\"}");
        Check(kwA.Equals(kwB) && kwA.GetHashCode() == kwB.GetHashCode(), "equal keywords hash equal");
        Check(kwA != kwC, "keyword equality is case-sensitive");
        Check(kwA.ToString() == "Keyword(:Status/Active)", "keyword formatting: " + kwA);

        var stringLikeUuid = DecodeSingle("\"" + BobUuidText + "\"");
        Check(refA != stringLikeUuid && refA.Kind != stringLikeUuid.Kind,
            "typed Ref never equals a same-looking String");
    }

    // --- helpers ---------------------------------------------------------

    private static MinigrafPreparedDatabase Seeded()
    {
        var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute($$"""(transact [[:alice :name "Alice"] [:alice :friend #uuid "{{BobUuidText}}"] [:alice :status :Status/Active]])""");
        return db;
    }

    private static MinigrafValue One(MinigrafPreparedDatabase db, string query) =>
        ((MinigrafQueryResult)db.Execute(query)).Rows[0][0];

    private static Guid EntityOf(MinigrafPreparedDatabase db)
    {
        string query = "(query [:find ?e :where [?e :name \"Alice\"]])";
        return ((MinigrafQueryResult)db.Execute(query)).Rows[0][0].GetRef();
    }

    private static MinigrafValue DecodeSingle(string cellJson)
    {
        string json = "{\"variables\":[\"?v\"],\"results\":[[" + cellJson + "]]}";
        var result = (MinigrafQueryResult)MinigrafResultDecoder.Decode(json);
        return result.Rows[0][0];
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
        catch
        {
            return false;
        }
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
