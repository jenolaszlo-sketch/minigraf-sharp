using MinigrafSharp;

internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> FailingCases = [];
    private static readonly List<string> CleanupDirs = [];

    private static void Main()
    {
        Console.WriteLine("managed explicit write-transaction qualification (M3.2b):");
        Console.WriteLine("  surface: MinigrafPreparedDatabase.BeginWrite / MinigrafWriteTransaction");
        Console.WriteLine("  scope: host RID, net8.0 and net10.0");

        Cases();

        Console.WriteLine($"pass={_pass} fail={_fail}");
        Cleanup();
        if (_fail != 0)
        {
            Console.WriteLine("failed case: " + string.Join(", ", FailingCases));
            Environment.Exit(1);
        }

        Console.WriteLine("ALL WRITE-TRANSACTION QUALIFICATION TESTS PASSED");
    }

    private static void Cases()
    {
        Case("begin -> stage -> commit (visibility, raw ack, read-your-own-writes)", CommitVisibility);
        Case("two staged batches commit as one transaction", TwoBatchesOneCommit);
        Case("rollback discards staged writes and leaves the database usable", RollbackDiscards);
        Case("dispose without commit rolls back (never commits)", DisposeRollsBack);
        Case("commit is terminal: double commit/rollback/execute rejected", CommitTerminalGuards);
        Case("rollback is terminal: later commit/execute rejected", RollbackTerminalGuards);
        Case("double dispose is harmless; dispose after commit is harmless", DisposeIdempotent);
        Case("failed staging leaves the transaction usable and classifies the error", FailedStagingUsable);
        Case("failed commit applies nothing, is structured and terminal, db reusable", FailedCommit);
        Case("database disposal cascades an open transaction (rollback) on the owner thread", DatabaseCascade);
        Case("second BeginWrite while open is refused deterministically", SingleOpenTransaction);
        Case("prepared query inside a transaction sees committed facts only", PreparedInsideTransaction);
        Case("rules registered in a transaction are not transactional", RulesNotTransactional);
        Case("same-thread second transaction refused across databases (native thread-local guard)", SameThreadCrossDatabaseGuard);
        Case("separate databases run transactions on different threads", SeparateDatabasesDifferentThreads);
        Case("transaction is thread-affine at the managed boundary", ThreadAffinityManaged);
        Case("concurrent database write blocks until the transaction commits", ConcurrentWriteBlocks);
        Case("file-backed commit survives dispose and reopen (WAL replay)", FileBackedCommitReopen);
    }

    private static void CommitVisibility()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var tx = db.BeginWrite();
        string ack = tx.ExecuteRaw("""(transact [[:alice :name "Alice"]])""");
        Check(ack.Contains("\"ok\":true"), "staged write acknowledges with an ok result: " + ack);
        Check(Rows(tx).Count == 1, "transactional read sees the staged row");
        tx.Commit();
        Check(Rows(db).Count == 1, "committed data is visible after commit");
        Check(Rows(db)[0]["?name"].GetString() == "Alice", "committed value");
    }

    private static void TwoBatchesOneCommit()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        tx.Execute("""(transact [[:b :v 2]])""");
        tx.Commit();
        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 2, "both staged batches committed");
    }

    private static void RollbackDiscards()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using (var tx = db.BeginWrite())
        {
            tx.Execute("""(transact [[:a :v 1]])""");
            tx.Rollback();
        }

        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 0, "rolled-back mutation is absent");
        using var again = db.BeginWrite();
        again.Execute("""(transact [[:b :v 2]])""");
        again.Commit();
        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 1, "database usable after rollback");
    }

    private static void DisposeRollsBack()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        tx.Dispose();
        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 0, "dispose did not commit");
        Expect<ObjectDisposedException>(() => tx.Execute("""(transact [[:b :v 2]])"""), "terminal after dispose");
    }

    private static void CommitTerminalGuards()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        tx.Commit();
        Expect<ObjectDisposedException>(tx.Commit, "second commit rejected");
        Expect<ObjectDisposedException>(tx.Rollback, "rollback after commit rejected");
        Expect<ObjectDisposedException>(
            () => tx.Execute("""(transact [[:b :v 2]])"""), "execute after commit rejected");
        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 1,
            "state after guard probes unchanged");
    }

    private static void RollbackTerminalGuards()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        tx.Rollback();
        Expect<ObjectDisposedException>(tx.Commit, "commit after rollback rejected");
        Expect<ObjectDisposedException>(
            () => tx.Execute("""(transact [[:b :v 2]])"""), "execute after rollback rejected");
        Check(Rows(db).Count == 0, "rolled-back state");
    }

    private static void DisposeIdempotent()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var committed = db.BeginWrite();
        committed.Execute("""(transact [[:a :v 1]])""");
        committed.Commit();
        committed.Dispose();
        committed.Dispose();
        var rolledBack = db.BeginWrite();
        rolledBack.Execute("""(transact [[:b :v 2]])""");
        rolledBack.Rollback();
        rolledBack.Dispose();
        rolledBack.Dispose();
        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 1,
            "only the committed transaction is present");
    }

    private static void FailedStagingUsable()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        var error = Expect<MinigrafExecutionException>(
            () => tx.Execute("""(transact [[:broken"""), "malformed command rejected");
        Check(error.NativeCategory == MinigrafNativeErrorCategory.Parser,
            "structured category Parser, got " + error.NativeCategory);
        Check(error.NativeCode is not null && error.NativeCode.StartsWith("PRS-", StringComparison.Ordinal),
            "stable native code preserved, got " + error.NativeCode);
        tx.Execute("""(transact [[:b :v 2]])""");
        tx.Commit();
        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 2,
            "earlier staged facts survive the failed staging");
    }

    private static void FailedCommit()
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
            Check(error.NativeCategory != MinigrafNativeErrorCategory.Unknown ||
                  error.NativeCode is not null,
                "commit failure carries structured information (" + error.NativeCategory + "/" +
                error.NativeCode + ")");
            Expect<ObjectDisposedException>(
                () => tx.Execute("""(transact [[:b :v 2]])"""), "failed commit is terminal");
            tx.Dispose();
            Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 0,
                "failed commit applied nothing");
        }
        finally
        {
            Directory.Delete(wal, recursive: false);
        }

        using (var retry = db.BeginWrite())
        {
            retry.Execute("""(transact [[:c :v 3]])""");
            retry.Commit();
        }

        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 1,
            "database and handle reusable after failed commit");
    }

    private static void DatabaseCascade()
    {
        string path = FileFix("cascade");
        var db = MinigrafPreparedDatabase.Open(path);
        var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        db.Dispose();
        Expect<ObjectDisposedException>(
            () => tx.Execute("""(transact [[:b :v 2]])"""), "transaction terminal after database cascade");
        tx.Dispose();
        using var reopened = MinigrafPreparedDatabase.Open(path);
        Check(Rows(reopened, "(query [:find ?e :where [?e :v ?v]])").Count == 0,
            "cascade rolled the transaction back persistently");
    }

    private static void SingleOpenTransaction()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        using var first = db.BeginWrite();
        Expect<InvalidOperationException>(() => db.BeginWrite(), "second open transaction refused");
        first.Commit();
        using var second = db.BeginWrite();
        second.Commit();
        Check(true, "begin works again after terminalization");
    }

    private static void PreparedInsideTransaction()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        db.Execute("""(transact [[:committed :name "C"]])""");
        using var prepared = db.Prepare("(query [:find ?e :where [?e :name ?n]])");
        using var tx = db.BeginWrite();
        tx.Execute("""(transact [[:staged :name "S"]])""");
        var preparedRows = prepared.Execute(new Dictionary<string, MinigrafBindValue>()) as MinigrafQueryResult;
        Check(preparedRows is not null && preparedRows.Rows.Count == 1,
            "prepared execution sees committed facts only");
        Check(Rows(tx).Count == 2, "transactional Datalog read sees committed and staged facts");
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
        Check(rows.Count == 1, "rule registered inside the rolled-back transaction persists");
        Check(rows[0]["?y"].GetString() == ":b", "rule sees committed facts only");
    }

    private static void SameThreadCrossDatabaseGuard()
    {
        using var first = MinigrafPreparedDatabase.OpenMemory();
        using var second = MinigrafPreparedDatabase.OpenMemory();
        using var tx = first.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");
        Expect<InvalidOperationException>(
            () => second.BeginWrite(),
            "same-thread second transaction on another database refused (measured thread-local guard)");
        tx.Rollback();
        using var other = second.BeginWrite();
        other.Execute("""(transact [[:b :v 2]])""");
        other.Commit();
        Check(Rows(second, "(query [:find ?e :where [?e :v ?v]])").Count == 1,
            "second database usable after the first transaction terminalized");
    }

    private static void SeparateDatabasesDifferentThreads()
    {
        using var first = MinigrafPreparedDatabase.OpenMemory();
        using var second = MinigrafPreparedDatabase.OpenMemory();
        using var tx = first.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");

        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var other = second.BeginWrite();
                other.Execute("""(transact [[:b :v 2]])""");
                other.Commit();
            }
            catch (Exception ex)
            {
                workerError = ex;
            }
        });
        worker.Start();
        Check(worker.Join(TimeSpan.FromSeconds(10)), "second-database transaction thread completed");
        Check(workerError is null, "second database transaction succeeded on another thread: " + workerError?.Message);
        // The thread-local guard also refuses an implicit execute on the
        // transaction-owning thread even against a different database; the
        // second database's committed state is therefore verified after the
        // first transaction terminalizes.
        Expect<MinigrafExecutionException>(
            () => second.Execute("""(query [:find ?e :where [?e :v ?v]])"""),
            "implicit execute on the owning thread is refused even for another database (INT-001)");
        tx.Commit();
        Check(Rows(second, "(query [:find ?e :where [?e :v ?v]])").Count == 1,
            "second database transaction committed while the first was open");
        Check(Rows(first, "(query [:find ?e :where [?e :v ?v]])").Count == 1,
            "first database transaction committed");
    }

    private static void ThreadAffinityManaged()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var tx = db.BeginWrite();
        tx.Execute("""(transact [[:a :v 1]])""");

        Exception? crossThreadError = null;
        var worker = new Thread(() =>
        {
            try
            {
                tx.Execute("""(transact [[:b :v 2]])""");
            }
            catch (Exception ex)
            {
                crossThreadError = ex;
            }
        });
        worker.Start();
        worker.Join();
        Check(crossThreadError is InvalidOperationException,
            "cross-thread execute refused, got " + crossThreadError?.GetType().Name);

        Exception? disposeError = null;
        var disposer = new Thread(() =>
        {
            try
            {
                db.Dispose();
            }
            catch (Exception ex)
            {
                disposeError = ex;
            }
        });
        disposer.Start();
        disposer.Join();
        Check(disposeError is InvalidOperationException,
            "cross-thread database disposal with an open transaction refused, got " +
            disposeError?.GetType().Name);

        tx.Rollback();
        Check(Rows(db).Count == 0, "owner thread could still terminalize after refused cross-thread probes");
    }

    private static void ConcurrentWriteBlocks()
    {
        using var db = MinigrafPreparedDatabase.OpenMemory();
        var tx = db.BeginWrite();
        tx.Execute("""(transact [[:held :v 1]])""");

        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                db.Execute("""(transact [[:implicit :v 2]])""");
            }
            catch (Exception ex)
            {
                workerError = ex;
            }
        });
        worker.Start();
        bool completedEarly = worker.Join(TimeSpan.FromMilliseconds(250));
        Check(!completedEarly, "concurrent write blocked while the transaction was open");
        tx.Commit();
        Check(worker.Join(TimeSpan.FromSeconds(10)), "concurrent write completed after commit");
        Check(workerError is null, "concurrent write succeeded after commit: " + workerError?.Message);
        Check(Rows(db, "(query [:find ?e :where [?e :v ?v]])").Count == 2, "both writes present");
    }

    private static void FileBackedCommitReopen()
    {
        string path = FileFix("reopen");
        using (var db = MinigrafPreparedDatabase.Open(path))
        {
            using var tx = db.BeginWrite();
            tx.Execute("""(transact [[:persisted :name "P"]])""");
            tx.Commit();
        }

        using var reopened = MinigrafPreparedDatabase.Open(path);
        var rows = Rows(reopened);
        Check(rows.Count == 1 && rows[0]["?name"].GetString() == "P",
            "committed transaction survives dispose and reopen");
    }

    // --- helpers ---------------------------------------------------------

    private static IReadOnlyList<MinigrafRow> Rows(MinigrafPreparedDatabase db, string query =
        "(query [:find ?name :where [?e :name ?name]])")
    {
        var result = (MinigrafQueryResult)db.Execute(query);
        return result.Rows;
    }

    private static IReadOnlyList<MinigrafRow> Rows(MinigrafWriteTransaction tx, string query =
        "(query [:find ?e :where [?e :name ?n]])")
    {
        var result = (MinigrafQueryResult)tx.Execute(query);
        return result.Rows;
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

    private static string RootTemp { get; } =
        Path.Combine(Path.GetTempPath(), "MinigrafSharp.TransactionQualificationTests");

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
