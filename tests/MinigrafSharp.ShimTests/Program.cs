using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> FailingCases = new List<string>();
    private static readonly List<string> CleanupDirs = new List<string>();
    private static string RootTemp = "";

    private sealed class AssertEx : Exception { public AssertEx(string m) : base(m) { } }

    internal static class Shim
    {
        private const string Dll = "minigrafsharp_shim.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_shim_identity();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_open([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_open_in_memory();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mgs_close(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mgs_string_free(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_last_error(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_prepared_last_error(IntPtr pq);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_execute(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string datalog);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_prepare(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string datalog);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_prepared_slot_names(IntPtr pq);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_prepared_execute(IntPtr pq, [MarshalAs(UnmanagedType.LPStr)] string bindings_json);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mgs_prepared_query_free(IntPtr pq);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_begin_write(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_tx_execute(
            IntPtr tx, [MarshalAs(UnmanagedType.LPStr)] string datalog);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mgs_tx_commit(IntPtr tx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mgs_tx_rollback(IntPtr tx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mgs_write_tx_free(IntPtr tx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_last_error_json(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mgs_tx_last_error_json(IntPtr tx);
    }

    internal static class Official
    {
        private const string Dll = "minigraf.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minigraf_open([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minigraf_open_in_memory();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minigraf_close(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minigraf_execute(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string datalog);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minigraf_string_free(IntPtr s);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minigraf_last_error(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int minigraf_checkpoint(IntPtr db);
    }

    internal static void Main()
    {
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, TryResolveHarnessNative);

        try
        {
            RootTemp = Path.Combine(Path.GetTempPath(), "MinigrafSharp.ShimTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootTemp);

            Case("gate", Gate);
            Case("identity token byte-exact", IdentityTokenByteExact);
            Case("identity token pointer stable", IdentityStable);
            Case("negative: official binary rejected under shim identity", OfficialBinaryNegativeGate);
            Case("official binary positive control", OfficialPositiveControl);
            Case("lifecycle and execute parity smoke", ParitySmoke);
            Case("prepare and slot names", PrepareAndSlotNames);
            Case("prepared execute semantics (val/entity/ref/keyword/freshness)", PreparedExecute);
            Case("temporal slots and literal keep", TemporalSlots);
            Case("extra bindings ignored", ExtraBindings);
            Case("error paths", ErrorPaths);
            Case("disposal", Disposal);
            Case("cross-library file handoff (official -> shim -> official)", CrossLibraryHandoff);
            Case("cross-library concurrent same-file open rejected", CrossLibraryConcurrent);
            Case("write transaction native lifecycle", TxLifecycle);
            Case("write transaction terminal misuse is MGS-002", TxTerminalMisuse);
            Case("write transaction stale error clearing", TxStaleErrorClearing);
            Case("write transaction failed commit diagnostics survive terminalization", TxFailedCommitDiagnostics);
            Case("write transaction thread affinity enforced in shim", TxThreadAffinity);
            Case("write transaction begin reentrancy is INT-001", TxBeginReentrancy);
        }
        finally
        {
            Cleanup();
        }

        Console.WriteLine();
        Console.WriteLine($"pass={_pass} fail={_fail}");
        foreach (var f in FailingCases)
        {
            Console.WriteLine($"failed case: {f}");
        }
        if (_fail == 0)
        {
            Console.WriteLine($"ALL {_pass} SHIM TESTS PASSED");
        }
        else
        {
            Environment.Exit(1);
        }
    }

    private static string HostRid()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) { return "win-x64"; }
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)) { return "osx-arm64"; }
        return "linux-x64";
    }

    private static string NativeFileName(string libraryName)
    {
        string baseName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName[..^4]
            : libraryName;
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) { return baseName + ".dll"; }
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)) { return "lib" + baseName + ".dylib"; }
        return "lib" + baseName + ".so";
    }

    private static IntPtr TryResolveHarnessNative(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "runtimes", HostRid(), "native", NativeFileName(libraryName));
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var loaded))
        {
            return loaded;
        }
        return IntPtr.Zero;
    }

    private static string? TakeOwnedString(IntPtr p)
    {
        if (p == IntPtr.Zero)
        {
            return null;
        }

        string? text = Marshal.PtrToStringUTF8(p);
        Shim.mgs_string_free(p);
        return text;
    }

    private static (string Category, string Code)? TakeErrorJson(IntPtr p)
    {
        string? text = TakeOwnedString(p);
        if (text is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        return (root.GetProperty("category").GetString() ?? string.Empty,
            root.GetProperty("code").GetString() ?? string.Empty);
    }

    private static void TxLifecycle()
    {
        IntPtr db = Shim.mgs_open_in_memory();
        Check(db != IntPtr.Zero, "in-memory open");
        IntPtr tx = Shim.mgs_begin_write(db);
        Check(tx != IntPtr.Zero, "begin write");

        string? ack = TakeOwnedString(Shim.mgs_tx_execute(tx, """(transact [[:a :v 1]])"""));
        Check(ack is not null && ack.Contains("\"ok\":true", StringComparison.Ordinal),
            "staged write acknowledges ok: " + ack);
        string? read = TakeOwnedString(Shim.mgs_tx_execute(tx, "(query [:find ?v :where [:a :v ?v]])"));
        Check(read is not null && read.Contains("\"results\"", StringComparison.Ordinal),
            "transactional read result shape: " + read);

        Check(Shim.mgs_tx_commit(tx) == 0, "commit returns 0");
        Check(Shim.mgs_tx_last_error_json(tx) == IntPtr.Zero,
            "successful commit leaves no structured error on the wrapper");
        Check(Shim.mgs_write_tx_free(tx) == 0, "free terminalized wrapper");

        string? after = TakeOwnedString(Shim.mgs_execute(db, "(query [:find ?v :where [:a :v ?v]])"));
        Check(after is not null && after.Contains("1", StringComparison.Ordinal),
            "committed fact visible after free: " + after);
        Shim.mgs_close(db);
        Check(Shim.mgs_write_tx_free(IntPtr.Zero) == -1, "free(NULL) is refused");
    }

    private static void TxTerminalMisuse()
    {
        IntPtr db = Shim.mgs_open_in_memory();
        IntPtr tx = Shim.mgs_begin_write(db);
        Check(tx != IntPtr.Zero, "begin write");
        Check(Shim.mgs_tx_commit(tx) == 0, "commit");

        Check(Shim.mgs_tx_execute(tx, """(transact [[:b :v 2]])""") == IntPtr.Zero,
            "execute after commit returns NULL");
        var executeError = TakeErrorJson(Shim.mgs_tx_last_error_json(tx));
        Check(executeError is { Category: "Shim", Code: "MGS-002" },
            "terminal misuse classified Shim/MGS-002: " + executeError);
        Check(Shim.mgs_tx_commit(tx) == -1, "second commit refused");
        var commitError = TakeErrorJson(Shim.mgs_tx_last_error_json(tx));
        Check(commitError is { Category: "Shim", Code: "MGS-002" },
            "second commit classified Shim/MGS-002: " + commitError);
        Shim.mgs_tx_rollback(tx);
        var rollbackError = TakeErrorJson(Shim.mgs_tx_last_error_json(tx));
        Check(rollbackError is { Category: "Shim", Code: "MGS-002" },
            "rollback after terminal classified Shim/MGS-002: " + rollbackError);
        Check(Shim.mgs_write_tx_free(tx) == 0, "free");
        Shim.mgs_close(db);
    }

    private static void TxStaleErrorClearing()
    {
        IntPtr db = Shim.mgs_open_in_memory();
        IntPtr tx = Shim.mgs_begin_write(db);
        Check(Shim.mgs_tx_execute(tx, """(transact [[:broken""") == IntPtr.Zero,
            "malformed command returns NULL");
        var error = TakeErrorJson(Shim.mgs_tx_last_error_json(tx));
        Check(error is { Category: "Parser" } &&
              error.Value.Code.StartsWith("PRS-", StringComparison.Ordinal),
            "parse error structured: " + error);
        Check(TakeOwnedString(Shim.mgs_tx_execute(tx, """(transact [[:a :v 1]])""")) is not null,
            "transaction remains usable");
        Check(Shim.mgs_tx_last_error_json(tx) == IntPtr.Zero,
            "successful operation cleared the previous structured error");
        Check(Shim.mgs_tx_commit(tx) == 0, "commit");
        Check(Shim.mgs_write_tx_free(tx) == 0, "free");
        Shim.mgs_close(db);
    }

    private static void TxFailedCommitDiagnostics()
    {
        string path = Path.Combine(RootTemp, "tx-commit-fail-" + Guid.NewGuid().ToString("N") + ".graph");
        string wal = path + ".wal";
        IntPtr db = Shim.mgs_open(path);
        Check(db != IntPtr.Zero, "file open");
        Directory.CreateDirectory(wal);
        try
        {
            IntPtr tx = Shim.mgs_begin_write(db);
            Check(tx != IntPtr.Zero, "begin write");
            Check(TakeOwnedString(Shim.mgs_tx_execute(tx, """(transact [[:a :v 1]])""")) is not null,
                "stage");

            Check(Shim.mgs_tx_commit(tx) == -1, "commit fails with the WAL blocked");
            var error = TakeErrorJson(Shim.mgs_tx_last_error_json(tx));
            Check(error is { Category: "Internal" } &&
                  error.Value.Code.StartsWith("INT-", StringComparison.Ordinal),
                "failed commit diagnostics retained after terminalization: " + error);
            Check(Shim.mgs_write_tx_free(tx) == 0, "free after failed commit");
            string? rows = TakeOwnedString(Shim.mgs_execute(db, "(query [:find ?e :where [?e :v ?v]])"));
            Check(rows is not null && rows.Contains("\"results\":[]", StringComparison.Ordinal),
                "failed commit applied nothing: " + rows);
            Shim.mgs_close(db);
        }
        finally
        {
            Directory.Delete(wal);
        }
    }

    private static void TxThreadAffinity()
    {
        IntPtr db = Shim.mgs_open_in_memory();
        IntPtr tx = Shim.mgs_begin_write(db);
        Check(TakeOwnedString(Shim.mgs_tx_execute(tx, """(transact [[:a :v 1]])""")) is not null,
            "stage on owner thread");

        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                Check(Shim.mgs_tx_execute(tx, """(transact [[:b :v 2]])""") == IntPtr.Zero,
                    "cross-thread execute refused");
                var executeError = TakeErrorJson(Shim.mgs_tx_last_error_json(tx));
                Check(executeError is { Category: "Shim", Code: "MGS-001" },
                    "cross-thread execute classified Shim/MGS-001: " + executeError);
                Check(Shim.mgs_tx_commit(tx) == -1, "cross-thread commit refused");
                var commitError = TakeErrorJson(Shim.mgs_tx_last_error_json(tx));
                Check(commitError is { Category: "Shim", Code: "MGS-001" },
                    "cross-thread commit classified Shim/MGS-001: " + commitError);
                Check(Shim.mgs_write_tx_free(tx) == -1, "cross-thread free of an open transaction refused");
                var freeError = TakeErrorJson(Shim.mgs_tx_last_error_json(tx));
                Check(freeError is { Category: "Shim", Code: "MGS-001" },
                    "cross-thread free classified Shim/MGS-001: " + freeError);
            }
            catch (Exception ex)
            {
                workerError = ex;
            }
        });
        worker.Start();
        worker.Join();
        Check(workerError is null, "worker assertions: " + workerError?.Message);

        // The wrapper survived the refusals; the owner can still finish.
        Check(TakeOwnedString(Shim.mgs_tx_execute(tx, """(transact [[:c :v 3]])""")) is not null,
            "owner thread can still stage");
        Check(Shim.mgs_tx_commit(tx) == 0, "owner commit succeeds after refused cross-thread probes");
        Check(Shim.mgs_write_tx_free(tx) == 0, "free from owner after terminalization");
        string? rows = TakeOwnedString(Shim.mgs_execute(db, "(query [:find ?v :where [?e :v ?v]])"));
        Check(rows is not null && CountResultRows(rows) == 2,
            "owner-staged facts committed: " + rows);
        Shim.mgs_close(db);
    }

    private static int CountResultRows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("results").GetArrayLength();
    }

    private static void TxBeginReentrancy()
    {
        IntPtr db = Shim.mgs_open_in_memory();
        IntPtr tx = Shim.mgs_begin_write(db);
        Check(tx != IntPtr.Zero, "begin write");
        Check(Shim.mgs_begin_write(db) == IntPtr.Zero, "second begin refused");
        var error = TakeErrorJson(Shim.mgs_last_error_json(db));
        Check(error is { Category: "Internal", Code: "INT-001" },
            "begin reentrancy classified Internal/INT-001: " + error);
        Shim.mgs_tx_rollback(tx);
        Check(Shim.mgs_write_tx_free(tx) == 0, "free");
        Shim.mgs_close(db);
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

    private static string UpstreamJsonPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "native", "upstream.json");
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
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "native", "shim", "identity.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new AssertEx("identity.json not found from " + AppContext.BaseDirectory);
    }

    private static IntPtr OpenMem()
    {
        var db = Shim.mgs_open_in_memory();
        Check(db != IntPtr.Zero, "mgs_open_in_memory returned NULL; last_error=" + DbError(IntPtr.Zero));
        return db;
    }

    private static string Exec(IntPtr db, string datalog)
    {
        var r = Shim.mgs_execute(db, datalog);
        Check(r != IntPtr.Zero, "mgs_execute returned NULL; last_error=" + DbError(db));
        return TakeString(r);
    }

    private static string DbError(IntPtr db)
    {
        var p = Shim.mgs_last_error(db);
        return p == IntPtr.Zero ? "(no last error)" : Marshal.PtrToStringUTF8(p)!;
    }

    private static string PreparedError(IntPtr pq)
    {
        var p = Shim.mgs_prepared_last_error(pq);
        return p == IntPtr.Zero ? "(no last error)" : Marshal.PtrToStringUTF8(p)!;
    }

    private static IntPtr Prepare(IntPtr db, string query)
    {
        var pq = Shim.mgs_prepare(db, query);
        Check(pq != IntPtr.Zero, "mgs_prepare returned NULL for '" + query + "'; last_error=" + DbError(db));
        return pq;
    }

    private static string SlotNames(IntPtr pq)
    {
        var r = Shim.mgs_prepared_slot_names(pq);
        Check(r != IntPtr.Zero, "mgs_prepared_slot_names returned NULL; last_error=" + PreparedError(pq));
        return TakeString(r);
    }

    private static string PExec(IntPtr pq, string bindings)
    {
        var r = Shim.mgs_prepared_execute(pq, bindings);
        Check(r != IntPtr.Zero, "mgs_prepared_execute returned NULL; last_error=" + PreparedError(pq));
        return TakeString(r);
    }

    private static string TakeString(IntPtr s)
    {
        var text = Marshal.PtrToStringUTF8(s);
        Shim.mgs_string_free(s);
        Check(text != null, "shim returned non-UTF8 string");
        return text!;
    }

    private static JsonElement JOf(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement ResultsOf(string json) => JOf(json).GetProperty("results");

    private static string FileFix(string name)
    {
        var dir = Path.Combine(RootTemp, name);
        Directory.CreateDirectory(dir);
        CleanupDirs.Add(dir);
        return Path.Combine(dir, name + ".graph");
    }

    private static void Cleanup()
    {
        var undeletable = new List<string>();
        foreach (var dir in CleanupDirs)
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
        while (guard != null)
        {
            if (string.Equals(guard.Name, "MinigrafSharp.ShimTests", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            guard = guard.Parent;
        }
        throw new AssertEx("cleanup guard violation: RootTemp is not under a disposable root");
    }

    private static void Gate()
    {
        Console.WriteLine("shim qualification target:");
        Console.WriteLine("  identity sources: native/shim/identity.json + native/upstream.json (byte-exact assert follows)");
        Console.WriteLine("  load path: runtimes/" + HostRid() + "/native/" + NativeFileName("minigrafsharp_shim.dll") + "; official " + NativeFileName("minigraf.dll") + " co-staged for gate + handoff cases");
    }

    private static void IdentityTokenByteExact()
    {
        var upstream = JsonDocument.Parse(File.ReadAllText(UpstreamJsonPath())).RootElement;
        var identity = JsonDocument.Parse(File.ReadAllText(IdentityJsonPath())).RootElement;
        string expected =
            "{\"shim_abi\":" + JsonSerializer.Serialize(identity.GetProperty("shim_abi").GetString()) +
            ",\"shim_source_revision\":" + identity.GetProperty("source_revision").GetInt64() +
            ",\"core_version\":" + JsonSerializer.Serialize(upstream.GetProperty("minigraf").GetProperty("version").GetString()) +
            ",\"core_commit\":" + JsonSerializer.Serialize(upstream.GetProperty("minigraf").GetProperty("commit").GetString()) + "}";
        var actual = Marshal.PtrToStringUTF8(Shim.mgs_shim_identity());
        Check(actual == expected, "identity token mismatch:\n  expected " + expected + "\n  actual   " + actual);
    }

    private static void IdentityStable()
    {
        var p1 = Shim.mgs_shim_identity();
        var p2 = Shim.mgs_shim_identity();
        Check(p1 != IntPtr.Zero && p1 == p2, "identity pointer changed between calls");
    }

    private static void OfficialBinaryNegativeGate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", HostRid(), "native", NativeFileName("minigraf.dll"));
        Check(File.Exists(path), "official engine binary not staged for probe: " + path);
        var lib = NativeLibrary.Load(path);
        try
        {
            try
            {
                NativeLibrary.GetExport(lib, "mgs_shim_identity");
                throw new AssertEx("mgs_shim_identity resolved on the official binary (identity gate violated)");
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }

    private static void OfficialPositiveControl()
    {
        var db = Official.minigraf_open_in_memory();
        Check(db != IntPtr.Zero, "official in_memory open failed");
        try
        {
            var r = Official.minigraf_execute(db, "(transact [[:alice :name \"Alice\"]])");
            Check(r != IntPtr.Zero, "official transact failed; minigraf_last_error=" + Marshal.PtrToStringUTF8(Official.minigraf_last_error(db)));
            var text = Marshal.PtrToStringUTF8(r)!;
            Official.minigraf_string_free(r);
            Check(text.StartsWith("{\"transacted\":"), "official transact shape: " + text);
        }
        finally { Official.minigraf_close(db); }
    }

    private static void ParitySmoke()
    {
        var db = OpenMem();
        try
        {
            var tx = Exec(db, "(transact [[:alice :name \"Alice\"] [:alice :age 30] [:bob :name \"Bob\"] [:bob :age 25]])");
            Check(tx.StartsWith("{\"transacted\":"), "transact result shape: " + tx);
            var q = Exec(db, "(query [:find ?n :where [?e :name ?n]])");
            var vars = JOf(q).GetProperty("variables");
            Check(vars.GetArrayLength() == 1 && vars[0].GetString() == "?n", "query variables: " + q);
            Check(JOf(q).GetProperty("results").GetArrayLength() == 2, "query rows: " + q);
        }
        finally { Shim.mgs_close(db); }
    }

    private static void PrepareAndSlotNames()
    {
        var db = OpenMem();
        try
        {
            Check(Exec(db, "(transact [[:alice :name \"Alice\"] [:alice :age 30]])").Contains("transacted"), "fixture");
            Check(SlotNames(Prepare(db, "(query [:find ?n :where [?e :name ?n]])")) == "[]", "slotless query");
            Check(SlotNames(Prepare(db, "(query [:find ?n :where [?e :name ?n] [?e :age $age]])")) == "[\"age\"]", "single slot");
            Check(SlotNames(Prepare(db, "(query [:find ?name :as-of $t :where [?e :name ?name] [?e :age $age] [?e :age $age]])")) == "[\"age\",\"t\"]", "multi slot sorted+dedup");

            var stringOnly = SlotNames(Prepare(db,
                "(query [:find ?e :where [?e :note \"cost is $5\"] [?e :liked $rel]])"));
            Check(stringOnly == "[\"rel\"]", "string content exclusion: " + stringOnly);

            var commentOnly = SlotNames(Prepare(db,
                "(query ; $head comment is skipped\n [:find ?e :where [?e :liked $rel]])"));
            Check(commentOnly == "[\"rel\"]", "comment exclusion: " + commentOnly);

            Check(SlotNames(Prepare(db, "(query [:find ?n :where [$who :name ?n]])")) == "[\"who\"]", "entity slot");
            Check(SlotNames(Prepare(db, "(query [:find ?name :valid-at $vt :where [?e :name ?name]])")) == "[\"vt\"]", "valid-at slot");
        }
        finally { Shim.mgs_close(db); }
    }

    private static void PreparedExecute()
    {
        var db = OpenMem();
        try
        {
            Check(Exec(db, "(transact [[:alice :name \"Alice\"] [:alice :age 30] [:bob :name \"Bob\"] [:bob :age 25]])").Contains("transacted"), "fixture tx 1");
            var pq = Prepare(db, "(query [:find ?name :where [?e :name ?name] [?e :age $age]])");

            var bobRow = PExec(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"val\",\"type\":\"integer\",\"value\":25}}]");
            Check(ResultsOf(bobRow)[0][0].GetString() == "Bob", "val int 25 -> Bob: " + bobRow);
            var aliceRow = PExec(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"val\",\"type\":\"integer\",\"value\":30}}]");
            Check(ResultsOf(aliceRow)[0][0].GetString() == "Alice", "val int 30 -> Alice: " + aliceRow);
            var empty = PExec(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"val\",\"type\":\"integer\",\"value\":31}}]");
            Check(ResultsOf(empty).GetArrayLength() == 0, "val int 31 -> empty: " + empty);

            Check(Exec(db, "(transact [[:carol :name \"Carol\"] [:carol :age 40]])").Contains("transacted"), "second fixture");
            var carolRow = PExec(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"val\",\"type\":\"integer\",\"value\":40}}]");
            Check(ResultsOf(carolRow)[0][0].GetString() == "Carol", "fresh state visible to prepared query: " + carolRow);

            var whoRow = ResultsOf(Exec(db, "(query [:find ?e :where [?e :name \"Alice\"]])"))[0][0].GetString()!;
            var pqEntity = Prepare(db, "(query [:find ?n :where [$who :name ?n]])");
            var entityRows = PExec(pqEntity, "[{\"name\":\"who\",\"bind\":{\"kind\":\"entity\",\"value\":\"" + whoRow + "\"}}]");
            Check(ResultsOf(entityRows)[0][0].GetString() == "Alice", "entity bind: " + entityRows);

            Check(Exec(db, "(transact [[:alice :friend :bob]])").Contains("transacted"), "ref fixture");
            var bobUuid = UpstreamKeywordUuid(":bob");
            var pqRef = Prepare(db, "(query [:find ?n :where [?e :friend $fr] [?e :name ?n]])");
            var refRows = PExec(pqRef, "[{\"name\":\"fr\",\"bind\":{\"kind\":\"ref\",\"value\":\"" + bobUuid + "\"}}]");
            Check(ResultsOf(refRows)[0][0].GetString() == "Alice", "ref bind: " + refRows);

            Check(Exec(db, "(transact [[:alice :status :active]])").Contains("transacted"), "keyword fixture");
            var pqKw = Prepare(db, "(query [:find ?e :where [?e :status $kw]])");
            var kwRows = PExec(pqKw, "[{\"name\":\"kw\",\"bind\":{\"kind\":\"keyword\",\"value\":\":active\"}}]");
            Check(ResultsOf(kwRows).GetArrayLength() == 1, "keyword bind rows: " + kwRows);
            var kwEmpty = PExec(pqKw, "[{\"name\":\"kw\",\"bind\":{\"kind\":\"keyword\",\"value\":\":sleeping\"}}]");
            Check(ResultsOf(kwEmpty).GetArrayLength() == 0, "keyword mismatch rows: " + kwEmpty);
        }
        finally { Shim.mgs_close(db); }
    }

    private static void TemporalSlots()
    {
        var db = OpenMem();
        try
        {
            var id1 = 0L; var id2 = 0L;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                id1 = JOf(Exec(db, "(transact [[:alice :name \"Alice\"]])")).GetProperty("transacted").GetInt64();
                System.Threading.Thread.Sleep(2);
                id2 = JOf(Exec(db, "(transact [[:bob :name \"Bob\"]])")).GetProperty("transacted").GetInt64();
                if (id2 > id1)
                {
                    break;
                }
                Shim.mgs_close(db);
                db = OpenMem();
                Check(db != IntPtr.Zero, "fresh probe db");
            }
            Check(id2 > id1, "engine tx ids are wall-clock ms; distinct ids required: id1=" + id1 + " id2=" + id2);
            Console.WriteLine($"  observed (tx ids): id1={id1} id2={id2}");

            var pq = Prepare(db, "(query [:find ?name :as-of $t :where [?e :name ?name]])");
            var at1 = PExec(pq, "[{\"name\":\"t\",\"bind\":{\"kind\":\"tx_count\",\"value\":1}}]");
            var at1Raw = Exec(db, "(query [:find ?name :as-of 1 :where [?e :name ?name]])");
            Check(ResultsOf(at1).GetArrayLength() == 1 && at1.Contains("Alice"), "as-of counter 1: " + at1);
            Check(at1 == at1Raw, "prepared TxCount(1) must equal raw :as-of 1:\n prepared " + at1 + "\n raw      " + at1Raw);
            var at2 = PExec(pq, "[{\"name\":\"t\",\"bind\":{\"kind\":\"tx_count\",\"value\":2}}]");
            var at2Raw = Exec(db, "(query [:find ?name :as-of 2 :where [?e :name ?name]])");
            Check(ResultsOf(at2).GetArrayLength() == 2, "as-of counter 2: " + at2);
            Check(at2 == at2Raw, "prepared TxCount(2) must equal raw :as-of 2:\n prepared " + at2 + "\n raw      " + at2Raw);

            var pqValidAt = Prepare(db, "(query [:find ?name :valid-at $vt :where [?e :name ?name]])");
            var past = PExec(pqValidAt, "[{\"name\":\"vt\",\"bind\":{\"kind\":\"timestamp\",\"value\":1000000}}]");
            Check(ResultsOf(past).GetArrayLength() == 0, "timestamp deep past: " + past);
            var future = PExec(pqValidAt, "[{\"name\":\"vt\",\"bind\":{\"kind\":\"timestamp\",\"value\":99999999999999}}]");
            Check(ResultsOf(future).GetArrayLength() == 2, "timestamp far future: " + future);
            var anyTime = PExec(pqValidAt, "[{\"name\":\"vt\",\"bind\":{\"kind\":\"any_valid_time\"}}]");
            Check(ResultsOf(anyTime).GetArrayLength() == 2, "any_valid_time: " + anyTime);

            var pqMixed = Prepare(db, "(query [:find ?name :as-of " + id1 + " :valid-at $vt :where [?e :name ?name]])");
            var mixedRows = PExec(pqMixed, "[{\"name\":\"vt\",\"bind\":{\"kind\":\"timestamp\",\"value\":253402300799000}}]");
            var rawEq = Exec(db, "(query [:find ?name :as-of " + id1 + " :valid-at \"9999-12-31T23:59:59Z\" :where [?e :name ?name]])");
            Check(mixedRows == rawEq, "literal as-of + slot valid-at must equal raw literal form:\n prepared " + mixedRows + "\n raw      " + rawEq);
        }
        finally { Shim.mgs_close(db); }
    }

    private static void ExtraBindings()
    {
        var db = OpenMem();
        try
        {
            Check(Exec(db, "(transact [[:alice :name \"Alice\"] [:alice :age 30]])").Contains("transacted"), "fixture");
            var pq = Prepare(db, "(query [:find ?name :where [?e :name ?name] [?e :age $age]])");
            var a = PExec(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"val\",\"type\":\"integer\",\"value\":30}}]");
            var b = PExec(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"val\",\"type\":\"integer\",\"value\":30}},{\"name\":\"not_a_slot\",\"bind\":{\"kind\":\"val\",\"type\":\"string\",\"value\":\"extra\"}}]");
            Check(a == b, "extra binding changed the result:\n a: " + a + "\n b: " + b);
        }
        finally { Shim.mgs_close(db); }
    }

    private static void ErrorPaths()
    {
        var db = OpenMem();
        try
        {
            Check(Exec(db, "(transact [[:alice :name \"Alice\"] [:alice :age 30]])").StartsWith("{\"transacted\":"), "fixture");

            Check(Shim.mgs_prepare(db, "(transact [[:alice :name \"Alice\"]])") == IntPtr.Zero, "transact prepare must fail");
            Check(Shim.mgs_last_error(db) != IntPtr.Zero, "db last_error must be set after failed prepare");
            Check(Shim.mgs_prepare(db, "not valid datalog !!!") == IntPtr.Zero, "parse failure prepare must fail");
            Check(Shim.mgs_prepare(db, "(query [:find ?v :where [?e $attr ?v]])") == IntPtr.Zero, "attribute slot must fail at prepare");

            var pq = Prepare(db, "(query [:find ?name :where [?e :name ?name] [?e :age $age]])");

            var missing = Shim.mgs_prepared_execute(pq, "[]");
            Check(missing == IntPtr.Zero, "missing slot must fail");
            var missingText = PreparedError(pq);
            Check(missingText.Length > 0, "prepared last_error must be set on missing slot");
            Console.WriteLine("  observed (missing slot): " + missingText);

            var wrongKind = Shim.mgs_prepared_execute(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"any_valid_time\"}}]");
            Check(wrongKind == IntPtr.Zero, "wrong kind must fail");
            var wrongKindText = PreparedError(pq);
            Check(wrongKindText.Length > 0, "prepared last_error must be set on wrong kind");
            Console.WriteLine("  observed (wrong kind): " + wrongKindText);

            var malformed = Shim.mgs_prepared_execute(pq, "[{]");
            Check(malformed == IntPtr.Zero, "malformed bindings JSON must fail");
            var malformedText = PreparedError(pq);
            Check(malformedText.Contains("invalid bindings JSON"), "bindings parse diagnostic: " + malformedText);

            var unknownKind = Shim.mgs_prepared_execute(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"weird\"}}]");
            Check(unknownKind == IntPtr.Zero, "unknown bind kind must fail");

            var infFloat = Shim.mgs_prepared_execute(pq, "[{\"name\":\"age\",\"bind\":{\"kind\":\"val\",\"type\":\"float\",\"value\":1e999}}]");
            Check(infFloat == IntPtr.Zero, "non-finite float must be rejected at the parse layer");
        }
        finally { Shim.mgs_close(db); }
    }

    private static void Disposal()
    {
        var db = OpenMem();
        try
        {
            Check(Exec(db, "(transact [[:alice :name \"Alice\"]])").Contains("transacted"), "fixture");
            var pq = Prepare(db, "(query [:find ?n :where [?e :name ?n]])");
            Check(SlotNames(pq) == "[]", "pre-free probe");
            Shim.mgs_prepared_query_free(pq);
            Shim.mgs_prepared_query_free(IntPtr.Zero);
            Check(Exec(db, "(query [:find ?n :where [?e :name ?n]])").Contains("Alice"), "db still usable after prepared query free");
            Check(Exec(db, "(transact [[:bob :name \"Bob\"]])").Contains("transacted"), "db still writable after prepared query free");
            Shim.mgs_close(IntPtr.Zero);
        }
        finally { Shim.mgs_close(db); }
    }

    private static void CrossLibraryHandoff()
    {
        var fileA = FileFix("handoff_a");
        var fileB = FileFix("handoff_b");

        var officialDb = Official.minigraf_open(fileA);
        Check(officialDb != IntPtr.Zero, "official open fixture");
        try
        {
            var r = Official.minigraf_execute(officialDb, "(transact [[:carol :name \"Carol\"]])");
            Check(r != IntPtr.Zero, "official writer transact");
            Official.minigraf_string_free(r);
            Check(Official.minigraf_checkpoint(officialDb) == 0, "official checkpoint");
        }
        finally { Official.minigraf_close(officialDb); }

        var shimHandle = Shim.mgs_open(fileA);
        Check(shimHandle != IntPtr.Zero, "shim must open an official-owned file after close");
        string shimRead;
        try
        {
            shimRead = Exec(shimHandle, "(query [:find ?n :where [?e :name ?n]])");
        }
        finally { Shim.mgs_close(shimHandle); }
        Check(shimRead.Contains("Carol"), "shim must read official-written content: " + shimRead);

        var shimWriter = Shim.mgs_open(fileB);
        Check(shimWriter != IntPtr.Zero, "shim open file fixture");
        try
        {
            Check(Exec(shimWriter, "(transact [[:dave :name \"Dave\"]])").Contains("transacted"), "shim transact");
        }
        finally { Shim.mgs_close(shimWriter); }

        var officialReader = Official.minigraf_open(fileB);
        Check(officialReader != IntPtr.Zero, "official open shim-owned file after close");
        string officialRead;
        try
        {
            var r2 = Official.minigraf_execute(officialReader, "(query [:find ?n :where [?e :name ?n]])");
            Check(r2 != IntPtr.Zero, "official query");
            officialRead = Marshal.PtrToStringUTF8(r2)!;
            Official.minigraf_string_free(r2);
        }
        finally { Official.minigraf_close(officialReader); }
        Check(officialRead.Contains("Dave"), "official must read shim-written content: " + officialRead);
    }

    private static void CrossLibraryConcurrent()
    {
        var fileA = FileFix("concurrent_a");
        var fileB = FileFix("concurrent_b");

        var officialDb = Official.minigraf_open(fileA);
        Check(officialDb != IntPtr.Zero, "official open fixture");
        try
        {
            var shimAttempt = Shim.mgs_open(fileA);
            if (shimAttempt != IntPtr.Zero)
            {
                Shim.mgs_close(shimAttempt);
            }
            Check(shimAttempt == IntPtr.Zero, "shim open of official-owned file must be rejected while the official handle is alive");
        }
        finally { Official.minigraf_close(officialDb); }

        var shimDb = Shim.mgs_open(fileB);
        Check(shimDb != IntPtr.Zero, "shim open fixture");
        try
        {
            var officialAttempt = Official.minigraf_open(fileB);
            if (officialAttempt != IntPtr.Zero)
            {
                Official.minigraf_close(officialAttempt);
            }
            Check(officialAttempt == IntPtr.Zero, "official open of shim-owned file must be rejected while the shim handle is alive");
        }
        finally { Shim.mgs_close(shimDb); }
    }

    internal static string UpstreamKeywordUuid(string keyword)
    {
        byte[] oidNamespace = new byte[]
        {
            0x6b, 0xa7, 0xb8, 0x12, 0x9d, 0xad, 0x11, 0xd1,
            0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8,
        };
        byte[] digest = SHA1.HashData(oidNamespace.Concat(System.Text.Encoding.UTF8.GetBytes(keyword)).ToArray());
        byte[] uuid = digest.AsSpan(0, 16).ToArray();
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        string hex = Convert.ToHexString(uuid).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
