// M1d qualification: storage, recovery, and temporal probes on the real
// pinned engine. No new public API. Known-issue probes PASS when the
// documented v2 limitation is observed and FAIL with a requalify message
// when behavior changes. Destructive probes run only under the test-owned
// temp root, only in child processes, never in the runner itself.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MinigrafSharp;

// Child probe modes (fresh processes with their own loader state).
if (args.Length == 2 && args[0] == "--probe-write-die")
{
    return ProbeWriteDie(args[1]);
}
if (args.Length == 2 && args[0] == "--probe-checkpoint-window")
{
    return ProbeCheckpointWindow(args[1]);
}
if (args.Length == 2 && args[0] == "--probe-stage-torn")
{
    return ProbeStageTorn(args[1]);
}
if (args.Length == 2 && args[0] == "--probe-stage-gen3")
{
    return ProbeStageGen3(args[1]);
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

Console.WriteLine("qualification target: Minigraf 2.0.5 / minigraf-c 2.0.5, RID win-x64");

// Version gate: probes apply only to the pinned baseline (upstream.json is
// the authority; the pins are read, not assumed).
string coreVersion = "";
string cVersion = "";
string? upstreamJson = FindUpstreamJson();
if (upstreamJson is not null)
{
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(upstreamJson));
    coreVersion = manifest.RootElement.GetProperty("minigraf").GetProperty("version").GetString() ?? "";
    cVersion = manifest.RootElement.GetProperty("minigraf-c").GetProperty("version").GetString() ?? "";
}
Check(coreVersion == "2.0.5", "gate Minigraf 2.0.5");
Check(cVersion == "2.0.5", "gate minigraf-c 2.0.5");

// Q1. Same-transaction multi-valued facts (upstream #371).
using (var q1 = MinigrafDatabase.OpenMemory())
{
    q1.Execute("""(transact [[:e1 :tag "A"] [:e1 :tag "B"]])""");
    int sameTx = CountRows(q1, "(query [:find ?t :where [:e1 :tag ?t]])");
    Console.WriteLine($"  Q1 same-transaction visible rows: {sameTx}");
    q1.Execute("""(transact [[:e2 :tag "A"]])""");
    q1.Execute("""(transact [[:e2 :tag "B"]])""");
    int splitTx = CountRows(q1, "(query [:find ?t :where [:e2 :tag ?t]])");
    Console.WriteLine($"  Q1 split-transaction visible rows: {splitTx}");
    if (sameTx == 2)
    {
        Check(false, "Q1 FIXED? same-tx multi-value reads 2 — requalify and update docs");
    }
    else
    {
        Check(sameTx == 1, "Q1 same-tx multi-value shows known v2 loss (1 of 2)");
    }
    Check(splitTx == 2, "Q1 split-tx control reads both values");
}
string q1Path = Path.Combine(NewFixtureDir(), "q1.db");
try
{
    int before;
    using (var wdb = MinigrafDatabase.Open(q1Path))
    {
        wdb.Execute("""(transact [[:e1 :tag "A"] [:e1 :tag "B"]])""");
        before = CountRows(wdb, "(query [:find ?t :where [:e1 :tag ?t]])");
        wdb.Checkpoint();
    }
    using (var rdb = MinigrafDatabase.Open(q1Path))
    {
        int after = CountRows(rdb, "(query [:find ?t :where [:e1 :tag ?t]])");
        Console.WriteLine($"  Q1 file visible rows: {before} -> {after}");
        // Both facts were durable all along: the live index view loses one
        // (#371), but the checkpoint rebuilds indexes from fact pages and
        // both reappear. Pin both sides; any change means requalify.
        Check(before == 1 && after == 2, "Q1 checkpoint/reopen restores hidden same-tx value");
    }
}
finally
{
    CleanupDirChecked(Path.GetDirectoryName(q1Path)!, "Q1");
}

// Q2. Retract then re-assert via Datalog (upstream #477 is WriteTransaction-level).
using (var q2 = MinigrafDatabase.OpenMemory())
{
    q2.Execute("""(transact [[:x :v "1"]])""");
    q2.Execute("""(retract [[:x :v "1"]])""");
    q2.Execute("""(transact [[:x :v "1"]])""");
    int present = CountRows(q2, "(query [:find ?v :where [:x :v ?v]])");
    Check(present == 1, "Q2 Datalog retract/retransact keeps the fact");
    Console.WriteLine("  Q2 core WriteTransaction-level issue not reachable via minigraf-c Datalog (no explicit tx surface)");
}

// Q3 + Q10. Valid-time windows and positive temporal suite (upstream #435 area).
using (var q3 = MinigrafDatabase.OpenMemory())
{
    q3.Execute("""(transact {:valid-from "2023-01-01" :valid-to "2023-06-30"} [[:alice :employment/status :active]])""");
    int inside = CountRows(q3, """(query [:find ?s :valid-at "2023-03-01" :where [:alice :employment/status ?s]])""");
    int beforeWindow = CountRows(q3, """(query [:find ?s :valid-at "2022-01-01" :where [:alice :employment/status ?s]])""");
    int afterWindow = CountRows(q3, """(query [:find ?s :valid-at "2024-01-01" :where [:alice :employment/status ?s]])""");
    int any = CountRows(q3, """(query [:find ?s :valid-at :any-valid-time :where [:alice :employment/status ?s]])""");
    Console.WriteLine($"  Q10 window reads before/inside/after/any: {beforeWindow}/{inside}/{afterWindow}/{any}");
    Check(inside == 1 && beforeWindow == 0 && afterWindow == 0 && any == 1, "Q10 window reads behave");

    q3.Execute("""(transact {:valid-from "2023-03-01" :valid-to "2023-09-30"} [[:alice :employment/status :inactive]])""");
    int overlap = CountRows(q3, """(query [:find ?s :valid-at "2023-04-01" :where [:alice :employment/status ?s]])""");
    Console.WriteLine($"  Q3 overlapping-window visible rows: {overlap}");
    if (overlap == 1)
    {
        Check(false, "Q3 FIXED? overlapping window replaced — requalify and update docs");
    }
    else
    {
        Check(overlap == 2, "Q3 overlapping windows both visible (known v2 non-replacement)");
    }

    string snapBefore = SnapshotStatuses(q3);
    q3.Checkpoint();
    string snapAfterCheckpoint = SnapshotStatuses(q3);
    Check(snapBefore == snapAfterCheckpoint, "Q3 observations stable across checkpoint");
}
string q3Path = Path.Combine(NewFixtureDir(), "q3.db");
try
{
    using (var wdb = MinigrafDatabase.Open(q3Path))
    {
        wdb.Execute("""(transact {:valid-from "2023-01-01" :valid-to "2023-06-30"} [[:alice :employment/status :active]])""");
        wdb.Checkpoint();
    }
    using (var rdb = MinigrafDatabase.Open(q3Path))
    {
        int reopened = CountRows(rdb, """(query [:find ?s :valid-at "2023-03-01" :where [:alice :employment/status ?s]])""");
        Check(reopened == 1, "Q10 temporal fixture survives checkpoint/reopen");
    }
}
finally
{
    CleanupDirChecked(Path.GetDirectoryName(q3Path)!, "Q3");
}

// Q10b. Transaction-time (:as-of) behavior.
using (var q10 = MinigrafDatabase.OpenMemory())
{
    q10.Execute("""(transact [[:alice :name "Alice"]])""");
    q10.Execute("""(transact [[:alice :name "Alicia"]])""");
    MinigrafQueryResult current = (MinigrafQueryResult)q10.Execute(
        "(query [:find ?name :where [:alice :name ?name]])");
    MinigrafQueryResult asOf1 = (MinigrafQueryResult)q10.Execute(
        "(query [:find ?name :as-of 1 :where [:alice :name ?name]])");
    Console.WriteLine($"  Q10 current={SingleString(current)}, as-of-1={SingleString(asOf1)}");
    // No attribute schema declares :name single-valued, so both assertions
    // stay live (consistent with Q1: facts accumulate, history included).
    Check(current.Rows.Count == 2, "Q10 current view keeps both assertions");
    Check(SingleString(asOf1) == "Alice", "Q10 :as-of 1 shows first-transaction view");
}

// Recursive regression control across reopen.
string recPath = Path.Combine(NewFixtureDir(), "rec.db");
try
{
    using (var wdb = MinigrafDatabase.Open(recPath))
    {
        wdb.Execute("""(transact [[:a :connected :b] [:b :connected :c]])""");
        wdb.Execute("(rule [(reachable ?x ?y) [?x :connected ?y]])");
        wdb.Execute("(rule [(reachable ?x ?y) [?x :connected ?z] (reachable ?z ?y)])");
        int direct = CountRows(wdb, "(query [:find ?y :where (reachable :a ?y)])");
        wdb.Checkpoint();
        Console.WriteLine($"  recursive reachable rows before reopen: {direct}");
        Check(direct == 2, "recursive rule derives transitively");
    }
    using (var rdb = MinigrafDatabase.Open(recPath))
    {
        int after;
        try
        {
            after = CountRows(rdb, "(query [:find ?y :where (reachable :a ?y)])");
            Console.WriteLine($"  recursive rows after reopen without re-register: {after}");
        }
        catch (MinigrafExecutionException ex)
        {
            after = -1;
            Console.WriteLine($"  recursive query after reopen throws (recorded): {FirstLine(ex.Message)}");
        }
        if (after == 2)
        {
            Check(false, "rules persisted across reopen? requalify and update docs");
        }
        else
        {
            Check(true, "rules are session-local (re-register after reopen)");
        }
        rdb.Execute("(rule [(reachable ?x ?y) [?x :connected ?y]])");
        rdb.Execute("(rule [(reachable ?x ?y) [?x :connected ?z] (reachable ?z ?y)])");
        Check(CountRows(rdb, "(query [:find ?y :where (reachable :a ?y)])") == 2,
            "re-registered rules derive over persisted facts");
    }
}
finally
{
    CleanupDirChecked(Path.GetDirectoryName(recPath)!, "recursion");
}

// Q13 control: normal close/reopen persists (close-time checkpoint, not crash recovery).
string ctlPath = Path.Combine(NewFixtureDir(), "ctl.db");
try
{
    using (var wdb = MinigrafDatabase.Open(ctlPath))
    {
        wdb.Execute("""(transact [[:c :v "kept"]])""");
    }
    using (var rdb = MinigrafDatabase.Open(ctlPath))
    {
        Check(CountRows(rdb, "(query [:find ?v :where [:c :v ?v]])") == 1,
            "Q13 normal close/reopen persists (control)");
    }
}
finally
{
    CleanupDirChecked(Path.GetDirectoryName(ctlPath)!, "Q13");
}

// Q13b. Clean disposal flushes the WAL (close-time checkpoint, default options).
string flushPath = Path.Combine(NewFixtureDir(), "flush.db");
try
{
    bool walWhileOpen;
    using (var wdb = MinigrafDatabase.Open(flushPath))
    {
        wdb.Execute("""(transact [[:c :v "kept"]])""");
        walWhileOpen = File.Exists(flushPath + ".wal");
    }
    bool walAfterDispose = File.Exists(flushPath + ".wal");
    Console.WriteLine($"  Q13b WAL while open={walWhileOpen} after dispose={walAfterDispose}");
    Check(walWhileOpen && !walAfterDispose, "Q13b clean dispose flushes WAL via close-time checkpoint");
}
finally
{
    CleanupDirChecked(Path.GetDirectoryName(flushPath)!, "Q13b");
}

// Q8. Truncated and degenerate files.
string q8dir = NewFixtureDir();
try
{
    string validPath = Path.Combine(q8dir, "valid.db");
    using (var wdb = MinigrafDatabase.Open(validPath))
    {
        wdb.Execute("""(transact [[:k :v "v"]])""");
        wdb.Checkpoint();
    }
    byte[] validBytes = File.ReadAllBytes(validPath);
    Console.WriteLine($"  Q8 valid .graph bytes: {validBytes.Length}");

    string shortPath = Path.Combine(q8dir, "short.db");
    File.WriteAllBytes(shortPath, validBytes[..100]);
    bool shortThrew = false;
    try
    {
        using var _ = MinigrafDatabase.Open(shortPath);
    }
    catch (MinigrafOpenException)
    {
        shortThrew = true;
    }
    Check(shortThrew, "Q8 truncated file fails open (never silent empty)");

    string zerosPath = Path.Combine(q8dir, "zeros.db");
    File.WriteAllBytes(zerosPath, new byte[100]);
    using (var zdb = MinigrafDatabase.Open(zerosPath))
    {
        int rows = CountRows(zdb, "(query [:find ?v :where [?e :v ?v]])");
        Check(rows == 0, "Q8 all-zeros short file opens as new empty database");
    }

    string emptyPath = Path.Combine(q8dir, "empty.db");
    File.WriteAllBytes(emptyPath, []);
    using (var edb = MinigrafDatabase.Open(emptyPath))
    {
        Check(CountRows(edb, "(query [:find ?v :where [?e :v ?v]])") == 0,
            "Q8 zero-length file opens as new empty database");
    }
}
finally
{
    CleanupDirChecked(q8dir, "Q8");
}

// Q9. Torn WAL on a copy: open must succeed, checkpointed generation visible.
// The writer is staged in a child (then killed) because Windows range-locks
// forbid copying the live files and clean disposal removes the WAL.
string q9dir = NewFixtureDir();
try
{
    bool staged;
    using (Process stager = StartProbe("--probe-stage-torn", q9dir))
    {
        staged = WaitForFile(Path.Combine(q9dir, "STAGED"), 30_000);
        KillChild(stager);
    }
    Check(staged, "Q9 stager reached checkpointed+WAL state");
    if (!staged)
    {
        goto Q9Skip;
    }
    string liveGraph = Path.Combine(q9dir, "victim.db");
    string liveWal = liveGraph + ".wal";
    Check(File.Exists(liveWal), "Q9 WAL survives abrupt stop");
    string copyGraph = Path.Combine(q9dir, "torn.db");
    string copyWal = copyGraph + ".wal";
    File.Copy(liveGraph, copyGraph);
    File.Copy(liveWal, copyWal);
    byte[] walBytes = File.ReadAllBytes(copyWal);
    Console.WriteLine($"  Q9 WAL bytes before torn truncate: {walBytes.Length}");
    File.WriteAllBytes(copyWal, walBytes[..(walBytes.Length / 2)]);
    using (var tdb = MinigrafDatabase.Open(copyGraph))
    {
        int aRows = CountRows(tdb, """(query [:find ?v :where [:t :a ?v]])""");
        int bRows = CountRows(tdb, """(query [:find ?v :where [:t :b ?v]])""");
        Console.WriteLine($"  Q9 rows after torn WAL: checkpointed A={aRows}, uncheckpointed B={bRows}");
        Check(aRows == 1, "Q9 torn WAL still opens with checkpointed generation");
    }
Q9Skip:;
}
finally
{
    CleanupDirChecked(q9dir, "Q9");
}

// Q5. Older graph restored beside a newer WAL (upstream #535).
// Genuine restore choreography: the graph advances to gen2, then the gen1
// bytes are restored over it while the gen3 WAL is retained.
string q5dir = NewFixtureDir();
try
{
    string livePath = Path.Combine(q5dir, "victim.db");
    using (var wdb = MinigrafDatabase.Open(livePath))
    {
        wdb.Execute("""(transact [[:gen :n "gen1"]])""");
        wdb.Checkpoint();
    }
    byte[] gen1Graph = File.ReadAllBytes(livePath);
    using (var wdb = MinigrafDatabase.Open(livePath))
    {
        wdb.Execute("""(transact [[:gen :n "gen2"]])""");
        wdb.Checkpoint();
    }
    bool staged;
    using (Process stager = StartProbe("--probe-stage-gen3", q5dir))
    {
        staged = WaitForFile(Path.Combine(q5dir, "STAGED"), 30_000);
        KillChild(stager);
    }
    Check(staged, "Q5 gen3 WAL staged");
    if (!staged)
    {
        goto Q5Skip;
    }
    byte[] gen3Wal = File.ReadAllBytes(livePath + ".wal");
    Console.WriteLine(LogManifest("Q5", "gen1-graph", gen1Graph.Length, gen1Graph));
    Console.WriteLine(LogManifest("Q5", "gen3-wal", gen3Wal.Length, gen3Wal));

    File.WriteAllBytes(livePath, gen1Graph);
    bool accepted = false;
    int mixedRows = -1;
    try
    {
        using var mdb = MinigrafDatabase.Open(livePath);
        mixedRows = CountRows(mdb, "(query [:find ?v :where [:gen :n ?v]])");
        accepted = true;
    }
    catch (MinigrafException ex)
    {
        Console.WriteLine($"  Q5 mismatch refused (recorded): {ex.GetType().Name}");
    }
    Console.WriteLine($"  Q5 old-graph + newer-WAL accepted={accepted} rows={mixedRows}");
    if (!accepted)
    {
        Check(false, "Q5 FIXED? generation mismatch refused — requalify and update docs");
    }
    else
    {
        Check(true, "Q5 v2 accepts old-graph + newer-WAL replay (known unsafe, restore with care)");
    }
Q5Skip:;
}
finally
{
    CleanupDirChecked(q5dir, "Q5");
}

// Q7. Abrupt termination after a returned write (5 rounds).
int q7Recovered = 0;
for (int round = 0; round < 5; round++)
{
    string dir = NewFixtureDir();
    try
    {
        using Process child = StartProbe("--probe-write-die", dir);
        if (!WaitForFile(Path.Combine(dir, "WRITE_RETURNED"), 30_000))
        {
            Console.WriteLine($"  Q7 round {round}: no WRITE_RETURNED marker");
            continue;
        }
        KillChild(child);
        using var rdb = MinigrafDatabase.Open(Path.Combine(dir, "victim.db"));
        if (CountRows(rdb, """(query [:find ?m :where [:victim :marker ?m]])""") == 1)
        {
            q7Recovered++;
        }
    }
    finally
    {
        CleanupDirChecked(dir, "Q7");
    }
}
Console.WriteLine($"  Q7 killed-after-write recovered: {q7Recovered}/5");
Check(q7Recovered == 5, "Q7 returned writes survive abrupt termination (5/5)");

// Q4-control: identical checkpoint sequence without termination.
string q4bdir = NewFixtureDir();
try
{
    using (var seed = MinigrafDatabase.Open(Path.Combine(q4bdir, "victim.db")))
    {
        seed.Execute("""(transact [[:w :v "A"]])""");
        seed.Checkpoint();
    }
    using (var wdb = MinigrafDatabase.Open(Path.Combine(q4bdir, "victim.db")))
    {
        wdb.Execute("""(transact [[:w :v "B"]])""");
        wdb.Checkpoint();
    }
    using (var rdb = MinigrafDatabase.Open(Path.Combine(q4bdir, "victim.db")))
    {
        Console.WriteLine($"  Q4-control values: {ValuesOf(rdb)}");
        Check(ValuesOf(rdb) == "A,B", "Q4-control checkpoint sequence keeps A+B without kill");
    }
}
finally
{
    CleanupDirChecked(q4bdir, "Q4-control");
}

// Q11. Point lookup semantics (in-memory). Uses valid query forms only:
// a value-bound pattern must still expose a variable to :find, so the
// lookup is [?e :attr literal] (find the entity) rather than binding both
// entity and value to constants (which binds nothing for :find).
using (var q11 = MinigrafDatabase.OpenMemory())
{
    q11.Execute("""(transact [[:s1 :k "one"]])""");
    q11.Execute("""(transact [[:s2 :k "a"]])""");
    q11.Execute("""(transact [[:s2 :k "b"]])""");
    q11.Execute("""(transact [[:s3 :n 1]])""");
    q11.Execute("""(transact [[:s3 :n 2]])""");
    int svScan = CountRows(q11, "(query [:find ?v :where [:s1 :k ?v]])");
    int svValueBound = CountRows(q11, """(query [:find ?e :where [?e :k "one"]])""");
    int mvScan = CountRows(q11, "(query [:find ?v :where [:s2 :k ?v]])");
    int mvValueBound = CountRows(q11, """(query [:find ?e :where [?e :k "a"]])""");
    int ivScan = CountRows(q11, "(query [:find ?v :where [:s3 :n ?v]])");
    int ivValueBound = CountRows(q11, "(query [:find ?e :where [?e :n 1]])");
    Console.WriteLine(
        $"  Q11 in-memory: single scan={svScan} valueBound={svValueBound}; " +
        $"multi scan={mvScan} valueBound={mvValueBound}; " +
        $"int scan={ivScan} valueBound={ivValueBound}");
    Check(svScan == 1 && svValueBound == 1, "Q11 single-valued entity/value lookup works");
    Check(mvScan == 2 && mvValueBound == 1, "Q11 multi-valued scan sees both; value-bound finds entity");
    Check(ivScan == 2 && ivValueBound == 1, "Q11 integer entity/value lookup works");
}

// Q11b. Same value-bound observations after checkpoint/reopen on a file DB.
string q11dir = NewFixtureDir();
try
{
    string q11Path = Path.Combine(q11dir, "q11.db");
    using (var wdb = MinigrafDatabase.Open(q11Path))
    {
        wdb.Execute("""(transact [[:s2 :k "a"]])""");
        wdb.Execute("""(transact [[:s2 :k "b"]])""");
        wdb.Checkpoint();
    }
    using (var rdb = MinigrafDatabase.Open(q11Path))
    {
        int scan = CountRows(rdb, "(query [:find ?v :where [:s2 :k ?v]])");
        int valueBound = CountRows(rdb, """(query [:find ?e :where [?e :k "a"]])""");
        Console.WriteLine($"  Q11b reopened scan={scan} valueBound={valueBound}");
        Check(scan == 2 && valueBound == 1, "Q11b reopened scan and value-bound lookup both work");
    }
}
finally
{
    CleanupDirChecked(q11dir, "Q11b");
}

// Q11c. Value-bound forms after two write/checkpoint sessions (mirrors the
// Q4 crash-fixture shape), to see whether the value-only form also degrades.
string q11cdir = NewFixtureDir();
try
{
    string q11cPath = Path.Combine(q11cdir, "q11c.db");
    using (var s1 = MinigrafDatabase.Open(q11cPath))
    {
        s1.Execute("""(transact [[:w :v "A"]])""");
        s1.Checkpoint();
    }
    using (var s2 = MinigrafDatabase.Open(q11cPath))
    {
        s2.Execute("""(transact [[:w :v "B"]])""");
        s2.Checkpoint();
    }
    using (var rdb = MinigrafDatabase.Open(q11cPath))
    {
        int scan = CountRows(rdb, "(query [:find ?v :where [:w :v ?v]])");
        int valueBound = CountRows(rdb, """(query [:find ?e :where [?e :v "A"]])""");
        Console.WriteLine($"  Q11c two-session reopened scan={scan} valueBound={valueBound}");
        Check(scan == 2 && valueBound == 1, "Q11c two-session reopened scan and value-bound both work");
    }
}
finally
{
    CleanupDirChecked(q11cdir, "Q11c");
}

// Q4. Abrupt termination during an active write/checkpoint loop (5 rounds).
// Honest scope: the native checkpoint is synchronous and opaque, so the kill
// can only land between managed operations, never provably inside the native
// call. The deterministic property qualified here is that a killed process
// does not corrupt the database: it reopens, previously checkpointed data
// survives, and the reopened database checkpoints cleanly. The loop's own
// progress is observed and reported, not asserted.
int q4Clean = 0;
var q4Random = new Random(20261012);
for (int round = 0; round < 5; round++)
{
    string dir = NewFixtureDir();
    try
    {
        using (var seed = MinigrafDatabase.Open(Path.Combine(dir, "victim.db")))
        {
            seed.Execute("""(transact [[:w :v "A"]])""");
            seed.Checkpoint();
        }
        using (Process child = StartProbe("--probe-checkpoint-window", dir))
        {
            if (!WaitForFile(Path.Combine(dir, "CHECKPOINT_STARTING"), 30_000))
            {
                Console.WriteLine($"  Q4 round {round}: loop never started");
                continue;
            }
            Thread.Sleep(q4Random.Next(200, 1500));
            KillChild(child);
        }
        string progressPath = Path.Combine(dir, "PROGRESS");
        string reached = File.Exists(progressPath) ? File.ReadAllText(progressPath).Trim() : "none";
        bool loopFinished = File.Exists(Path.Combine(dir, "CHECKPOINT_RETURNED"));
        bool reopened = false;
        bool priorSurvived = false;
        int loopBefore = 0;
        try
        {
            using var rdb = MinigrafDatabase.Open(Path.Combine(dir, "victim.db"));
            reopened = true;
            priorSurvived = ValuesOf(rdb).Split(',').Contains("A", StringComparer.Ordinal);
            loopBefore = LoopCount(rdb);
        }
        catch (MinigrafException ex)
        {
            Console.WriteLine($"  Q4 round {round}: reopen failed ({ex.GetType().Name})");
        }
        using (var heal = MinigrafDatabase.Open(Path.Combine(dir, "victim.db")))
        {
            heal.Checkpoint();
        }
        int loopAfter;
        using (var verify = MinigrafDatabase.Open(Path.Combine(dir, "victim.db")))
        {
            loopAfter = LoopCount(verify);
        }
        Console.WriteLine(
            $"  Q4 round {round}: killReachedLoop={reached} loopFinished={loopFinished} " +
            $"reopened={reopened} priorSurvived={priorSurvived} loopBefore={loopBefore} loopAfter={loopAfter}");
        // Prior checkpointed data must survive, and a post-kill checkpoint
        // must never reduce the visible fact set.
        if (reopened && priorSurvived && loopAfter >= loopBefore)
        {
            q4Clean++;
        }
    }
    finally
    {
        CleanupDirChecked(dir, "Q4");
    }
}
Console.WriteLine($"  Q4 checkpoint-loop kills preserve database: {q4Clean}/5");
Check(q4Clean == 5, "Q4 kills during checkpoint loop reopen cleanly and preserve prior data (5/5)");

Console.WriteLine(failures == 0 ? "ALL QUALIFICATION TESTS PASSED" : $"{failures} QUALIFICATION TEST(S) FAILED");
return failures == 0 ? 0 : 1;

// ---------- helpers ----------

static int CountRows(MinigrafDatabase db, string datalog)
{
    MinigrafQueryResult result = (MinigrafQueryResult)db.Execute(datalog);
    return result.Rows.Count;
}

static string SingleString(MinigrafQueryResult result) =>
    result.Rows.Count == 1 ? result.Rows[0][0].GetString() : $"<{result.Rows.Count} rows>";

static string ValuesOf(MinigrafDatabase db)
{
    MinigrafQueryResult result = (MinigrafQueryResult)db.Execute(
        "(query [:find ?v :where [:w :v ?v]])");
    return string.Join(",", result.Rows.Select(row => row[0].GetString()).Order(StringComparer.Ordinal));
}

static int LoopCount(MinigrafDatabase db) =>
    CountRows(db, "(query [:find ?v :where [?e :i ?v]])");

static string SnapshotStatuses(MinigrafDatabase db)
{
    MinigrafQueryResult before = (MinigrafQueryResult)db.Execute(
        """(query [:find ?s :valid-at "2023-02-01" :where [:alice :employment/status ?s]])""");
    MinigrafQueryResult during = (MinigrafQueryResult)db.Execute(
        """(query [:find ?s :valid-at "2023-04-01" :where [:alice :employment/status ?s]])""");
    MinigrafQueryResult after = (MinigrafQueryResult)db.Execute(
        """(query [:find ?s :valid-at "2023-08-01" :where [:alice :employment/status ?s]])""");
    static string Cell(MinigrafQueryResult r) =>
        r.Rows.Count == 0 ? "-" : string.Join("+", r.Rows.Select(row => row[0].GetString()));
    return $"{Cell(before)}/{Cell(during)}/{Cell(after)}";
}

static string NewFixtureDir()
{
    string dir = Path.Combine(
        Path.GetTempPath(), "MinigrafSharp.Qualification", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

static void AssertUnderRoot(string path)
{
    string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MinigrafSharp.Qualification"));
    string full = Path.GetFullPath(path);
    if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Refusing to touch path outside test root: {path}");
    }
}

static void DeleteFixtureDir(string dir)
{
    // Failures propagate: cleanup problems are evidence, not noise.
    AssertUnderRoot(dir);
    Directory.Delete(dir, recursive: true);
}

void CleanupDirChecked(string dir, string label)
{
    try
    {
        DeleteFixtureDir(dir);
    }
    catch (Exception ex)
    {
        Check(false, $"{label} cleanup released files ({ex.GetType().Name}: {ex.Message})");
    }
}

static string? FindUpstreamJson()
{
    string? dir = AppContext.BaseDirectory;
    for (int i = 0; i < 10 && dir is not null; i++)
    {
        string candidate = Path.Combine(dir, "native", "upstream.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    return null;
}

static string ShaHex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

static string FirstLine(string message)
{
    int newline = message.IndexOfAny(['\r', '\n']);
    return (newline < 0 ? message : message[..newline]).Trim();
}

static string LogManifest(string tag, string label, int bytes, byte[] content) =>
    $"  {tag} manifest: {label} size={bytes} sha256={ShaHex(content)}";

static Process StartProbe(string mode, string dir)
{
    string? exe = Environment.ProcessPath
        ?? throw new InvalidOperationException("Child probe executable unknown.");
    var psi = new ProcessStartInfo(exe, $"{mode} \"{dir}\"")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    return Process.Start(psi) ?? throw new InvalidOperationException("Child probe failed to start.");
}

static bool WaitForFile(string path, int timeoutMs)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (File.Exists(path))
        {
            return true;
        }
        Thread.Sleep(100);
    }
    return File.Exists(path);
}

static void KillChild(Process child)
{
    try
    {
        if (!child.HasExited)
        {
            child.Kill();
        }
    }
    finally
    {
        child.WaitForExit(15_000);
    }
}

static int ProbeWriteDie(string dir)
{
    AssertUnderRoot(dir);
    // Intentionally never disposed: the parent terminates this process to
    // simulate abrupt termination after a returned write.
    var db = MinigrafDatabase.Open(Path.Combine(dir, "victim.db"));
    db.Execute("""(transact [[:victim :marker "written"]])""");
    File.WriteAllText(Path.Combine(dir, "WRITE_RETURNED"), "1");
    Thread.Sleep(60_000);
    return 0;
}

static int ProbeCheckpointWindow(string dir)
{
    AssertUnderRoot(dir);
    using var db = MinigrafDatabase.Open(Path.Combine(dir, "victim.db"));
    // A long write+checkpoint loop widens the kill window. The parent kills
    // this process at a random point; the kill lands between managed
    // operations, never provably inside the synchronous, opaque native
    // checkpoint call. Each iteration writes a distinct entity so the loop
    // also avoids the multi-valued-attribute path qualified in Q1.
    File.WriteAllText(Path.Combine(dir, "CHECKPOINT_STARTING"), "1");
    for (int i = 0; i < 5000; i++)
    {
        db.Execute($"(transact [[:loop{i} :i {i}]])");
        db.Checkpoint();
        File.WriteAllText(Path.Combine(dir, "PROGRESS"), i.ToString());
    }
    File.WriteAllText(Path.Combine(dir, "CHECKPOINT_RETURNED"), "1");
    return 0;
}

static int ProbeStageTorn(string dir)
{
    AssertUnderRoot(dir);
    // Stays alive without disposing so the WAL survives; the parent kills
    // this process, then copies the files (Windows range-locks forbid
    // copying while the writer is alive).
    var db = MinigrafDatabase.Open(Path.Combine(dir, "victim.db"));
    db.Execute("""(transact [[:t :a "A"]])""");
    db.Checkpoint();
    db.Execute("""(transact [[:t :b "B"]])""");
    File.WriteAllText(Path.Combine(dir, "STAGED"), "1");
    Thread.Sleep(60_000);
    return 0;
}

static int ProbeStageGen3(string dir)
{
    AssertUnderRoot(dir);
    var db = MinigrafDatabase.Open(Path.Combine(dir, "victim.db"));
    db.Execute("""(transact [[:gen :n "gen3"]])""");
    File.WriteAllText(Path.Combine(dir, "STAGED"), "1");
    Thread.Sleep(60_000);
    return 0;
}
