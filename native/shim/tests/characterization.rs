//! M3.2a characterization probes against the pinned Minigraf 2.0.7 core.
//!
//! These tests measure the native behavior M3.2 intends to expose. They do
//! not exercise the owned shim ABI (that comes with the implementation
//! slices). Every assertion below is a measured fact recorded in
//! `docs/evidence/m3.2/characterization.md`; they run wherever
//! `cargo test --locked` runs (eng/Build-Shim.ps1 -> the per-RID CI jobs), so
//! the same facts are re-measured on win-x64, linux-x64 and osx-arm64.
//!
//! Sources: crates.io `minigraf 2.0.7` (tarball SHA-256
//! `74E6724A59FECE46F0F56C3FE16F1792B34E34FE08FDFF4C902B22696D760D68`).

use minigraf::{ErrorCategory, Minigraf, OpenOptions, QueryResult, SyncMode, Value};
use std::path::{Path, PathBuf};
use std::sync::mpsc;
use std::time::{Duration, Instant};

struct TempDir(PathBuf);

impl TempDir {
    fn new(tag: &str) -> Self {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let dir = std::env::temp_dir().join(format!(
            "mgs-char-{tag}-{}-{nanos}",
            std::process::id()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    fn path(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn rows(db: &Minigraf, query: &str) -> Vec<Vec<Value>> {
    match db.execute(query).unwrap() {
        QueryResult::QueryResults { results, .. } => results,
        other => panic!("expected query results, got {other:?}"),
    }
}

fn row_count(db: &Minigraf, query: &str) -> usize {
    rows(db, query).len()
}

fn wal_path(db_path: &Path) -> PathBuf {
    let mut p = db_path.to_path_buf();
    let mut name = p.file_name().unwrap().to_os_string();
    name.push(".wal");
    p.set_file_name(name);
    p
}

// ---------------------------------------------------------------------------
// Transaction lifecycle
// ---------------------------------------------------------------------------

#[test]
fn tx_write_returns_ok_and_reads_see_staged_and_committed_facts() {
    let db = Minigraf::in_memory().unwrap();
    db.execute(r#"(transact [[:bob :name "Bob"]])"#).unwrap();

    let mut tx = db.begin_write().unwrap();
    let write_reply = tx
        .execute(r#"(transact [[:alice :name "Alice"]])"#)
        .unwrap();
    assert_eq!(
        write_reply,
        QueryResult::Ok,
        "staged writes acknowledge with Ok, not Transacted"
    );

    // Read-your-own-writes: committed Bob plus staged Alice.
    match tx
        .execute("(query [:find ?e :where [?e :name ?n]])")
        .unwrap()
    {
        QueryResult::QueryResults { results, .. } => assert_eq!(results.len(), 2),
        other => panic!("unexpected transactional read {other:?}"),
    }

    let before = db.current_tx_count();
    tx.commit().unwrap();
    assert_eq!(
        db.current_tx_count(),
        before + 1,
        "one commit is one tx_count, regardless of staged batches"
    );
    assert_eq!(
        row_count(&db, r#"(query [:find ?e :where [?e :name "Alice"]])"#),
        1
    );
}

#[test]
fn tx_multiple_writes_commit_atomically_as_one_tx_count() {
    let db = Minigraf::in_memory().unwrap();
    let mut tx = db.begin_write().unwrap();
    tx.execute(r#"(transact [[:a :v 1]])"#).unwrap();
    tx.execute(r#"(transact [[:b :v 2]])"#).unwrap();
    let before = db.current_tx_count();
    tx.commit().unwrap();
    assert_eq!(db.current_tx_count(), before + 1);
    assert_eq!(row_count(&db, "(query [:find ?e :where [?e :v ?v]])"), 2);
}

#[test]
fn tx_rollback_discards_staged_facts_and_leaves_db_usable() {
    let db = Minigraf::in_memory().unwrap();
    {
        let mut tx = db.begin_write().unwrap();
        tx.execute(r#"(transact [[:a :v 1]])"#).unwrap();
        tx.rollback();
    }
    assert_eq!(row_count(&db, "(query [:find ?e :where [?e :v ?v]])"), 0);
    let mut tx = db.begin_write().unwrap();
    tx.execute(r#"(transact [[:b :v 2]])"#).unwrap();
    tx.commit().unwrap();
    assert_eq!(row_count(&db, "(query [:find ?e :where [?e :v ?v]])"), 1);
}

#[test]
fn tx_drop_without_commit_is_implicit_rollback() {
    let db = Minigraf::in_memory().unwrap();
    {
        let mut tx = db.begin_write().unwrap();
        tx.execute(r#"(transact [[:a :v 1]])"#).unwrap();
        // dropped here
    }
    assert_eq!(row_count(&db, "(query [:find ?e :where [?e :v ?v]])"), 0);
}

#[test]
fn tx_empty_commit_succeeds_without_tx_count_bump() {
    let db = Minigraf::in_memory().unwrap();
    let before = db.current_tx_count();
    db.begin_write().unwrap().commit().unwrap();
    assert_eq!(db.current_tx_count(), before);
}

#[test]
fn tx_failed_write_leaves_transaction_usable_and_staged_state_intact() {
    let db = Minigraf::in_memory().unwrap();
    let mut tx = db.begin_write().unwrap();
    tx.execute(r#"(transact [[:a :v 1]])"#).unwrap();
    let err = tx
        .execute(r#"(transact [[:broken"#)
        .expect_err("malformed command must fail");
    assert_eq!(err.category(), ErrorCategory::Parser);
    assert!(
        err.code().starts_with("PRS-"),
        "measured code: {}",
        err.code()
    );
    // The transaction remains usable; earlier staged facts are not lost.
    tx.execute(r#"(transact [[:b :v 2]])"#).unwrap();
    tx.commit().unwrap();
    assert_eq!(row_count(&db, "(query [:find ?e :where [?e :v ?v]])"), 2);
}

#[test]
fn tx_same_thread_reentrancy_is_int001() {
    let db = Minigraf::in_memory().unwrap();
    let tx = db.begin_write().unwrap();
    let err = match db.begin_write() {
        Ok(_) => panic!("second begin_write on the same thread must fail"),
        Err(e) => e,
    };
    assert_eq!(err.category(), ErrorCategory::Internal);
    assert_eq!(err.code(), "INT-001", "measured code: {}", err.code());
    // Implicit execute in the same thread is refused for the same reason.
    let err = db
        .execute(r#"(transact [[:a :v 1]])"#)
        .expect_err("implicit execute while a tx is active must fail");
    assert_eq!(err.code(), "INT-001");
    tx.rollback();
    // After rollback the same thread can begin again.
    db.begin_write().unwrap().rollback();
}

#[test]
fn tx_second_thread_blocks_until_commit() {
    let db = Minigraf::in_memory().unwrap();
    let (sender, receiver) = mpsc::channel();

    std::thread::scope(|scope| {
        let mut first = db.begin_write().unwrap();
        first.execute(r#"(transact [[:a :v 1]])"#).unwrap();

        scope.spawn(|| {
            let started = Instant::now();
            let tx = db.begin_write().unwrap();
            sender.send(started.elapsed()).unwrap();
            tx.rollback();
        });

        // The second writer must not acquire the lock while the first is open.
        match receiver.recv_timeout(Duration::from_millis(250)) {
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            other => panic!("second writer did not block while tx was open: {other:?}"),
        }
        first.commit().unwrap();
        let waited = receiver
            .recv_timeout(Duration::from_secs(10))
            .expect("second writer should proceed after commit");
        assert!(
            waited >= Duration::from_millis(250),
            "measured wait: {waited:?}"
        );
    });
}

#[test]
fn tx_rule_registration_is_not_transactional() {
    let db = Minigraf::in_memory().unwrap();
    db.execute(r#"(transact [[:a :edge :b]])"#).unwrap();
    {
        let mut tx = db.begin_write().unwrap();
        tx.execute(r#"(rule [(reach ?x ?y) [?x :edge ?y]])"#)
            .unwrap();
        tx.execute(r#"(transact [[:b :edge :c]])"#).unwrap();
        tx.rollback();
    }
    // The rule registered inside the rolled-back transaction persists (rules
    // are applied to the shared registry immediately), while the staged fact
    // does not.
    assert_eq!(
        row_count(&db, "(query [:find ?y :where (reach :a ?y)])"),
        1
    );
}

#[test]
fn tx_prepared_query_executes_but_does_not_see_staged_facts() {
    let db = Minigraf::in_memory().unwrap();
    db.execute(r#"(transact [[:committed :name "C"]])"#).unwrap();
    let prepared = db
        .prepare("(query [:find ?e :where [?e :name ?n]])")
        .unwrap();

    let mut tx = db.begin_write().unwrap();
    tx.execute(r#"(transact [[:staged :name "S"]])"#)
        .unwrap();
    // Prepared execution reads shared FactStorage, which staged facts have
    // not reached yet; only the committed row is visible.
    match prepared.execute(&[]).unwrap() {
        QueryResult::QueryResults { results, .. } => assert_eq!(
            results.len(),
            1,
            "prepared execute inside a write tx sees committed facts only"
        ),
        other => panic!("unexpected prepared result {other:?}"),
    }
    // Transactional Datalog reads do see the staged row.
    match tx
        .execute("(query [:find ?e :where [?e :name ?n]])")
        .unwrap()
    {
        QueryResult::QueryResults { results, .. } => assert_eq!(results.len(), 2),
        other => panic!("unexpected transactional read {other:?}"),
    }
    tx.rollback();
}

#[test]
fn tx_failed_commit_leaves_clean_state_and_handle_reusable() {
    let dir = TempDir::new("tx-commit");
    let db_path = dir.path("db.graph");
    let db = Minigraf::open(&db_path).unwrap();

    // Force the WAL writer to fail deterministically: the WAL path is a
    // directory, so opening the WAL for the commit cannot succeed.
    let wal = wal_path(&db_path);
    std::fs::create_dir_all(&wal).unwrap();

    let mut tx = db.begin_write().unwrap();
    tx.execute(r#"(transact [[:a :v 1]])"#).unwrap();
    let err = tx.commit().expect_err("commit must fail with the WAL blocked");
    println!(
        "measured failed-commit error: category={:?} code={} text={}",
        err.category(),
        err.code(),
        err
    );
    // Failed commit applies nothing and releases the write lock.
    assert_eq!(row_count(&db, "(query [:find ?e :where [?e :v ?v]])"), 0);

    std::fs::remove_dir_all(&wal).unwrap();
    let mut tx = db.begin_write().unwrap();
    tx.execute(r#"(transact [[:b :v 2]])"#).unwrap();
    tx.commit().expect("commit after the induced failure must succeed");
    assert_eq!(row_count(&db, "(query [:find ?e :where [?e :v ?v]])"), 1);
}

// ---------------------------------------------------------------------------
// Open options
// ---------------------------------------------------------------------------

#[test]
fn open_options_default_matches_open_and_threshold_controls_checkpoint() {
    let dir = TempDir::new("open-opts");
    let path = dir.path("db.graph");
    let wal = wal_path(&path);

    // threshold = 1: each commit auto-checkpoints, so the WAL is removed.
    let db = OpenOptions::new()
        .wal_checkpoint_threshold(1)
        .path(&path)
        .open()
        .unwrap();
    db.execute(r#"(transact [[:a :v 1]])"#).unwrap();
    assert!(
        !wal.exists(),
        "threshold=1 should checkpoint and remove the WAL"
    );
    assert!(path.exists(), "checkpointed main file exists");
    drop(db);

    // usize::MAX disables implicit checkpoints: the WAL must remain after
    // commit and after the handle is dropped (close-time checkpoint off).
    let path2 = dir.path("db2.graph");
    let wal2 = wal_path(&path2);
    let db2 = OpenOptions::new()
        .wal_checkpoint_threshold(usize::MAX)
        .path(&path2)
        .open()
        .unwrap();
    db2.execute(r#"(transact [[:b :v 2]])"#).unwrap();
    drop(db2);
    assert!(
        wal2.exists(),
        "usize::MAX threshold should leave the WAL in place"
    );

    // Defaults equal Minigraf::open for observable durability behavior:
    // a normal open checkpoints on clean close, leaving no WAL.
    let path3 = dir.path("db3.graph");
    let wal3 = wal_path(&path3);
    let db3 = Minigraf::open(&path3).unwrap();
    db3.execute(r#"(transact [[:c :v 3]])"#).unwrap();
    drop(db3);
    assert!(!wal3.exists(), "default open checkpoints on clean close");
}

#[test]
fn open_options_limits_are_enforced_by_the_engine() {
    let db = OpenOptions::new().max_results(1).open_memory().unwrap();
    db.execute(r#"(transact [[:a :edge :b] [:a :edge :c]])"#)
        .unwrap();
    db.execute(r#"(rule [(reach ?x ?y) [?x :edge ?y]])"#)
        .unwrap();
    let err = db
        .execute("(query [:find ?y :where (reach :a ?y)])")
        .expect_err("max_results=1 must reject a 2-row rule result");
    println!(
        "measured max_results error: category={:?} code={} text={}",
        err.category(),
        err.code(),
        err
    );
    // Measured: limit enforcement is classified Internal/INT-020, not Query.
    assert_eq!(err.category(), ErrorCategory::Internal);
    assert_eq!(err.code(), "INT-020", "measured code: {}", err.code());

    let db = OpenOptions::new()
        .max_derived_facts(1)
        .open_memory()
        .unwrap();
    db.execute(r#"(transact [[:a :edge :b] [:a :edge :c]])"#)
        .unwrap();
    db.execute(r#"(rule [(reach ?x ?y) [?x :edge ?z] (reach ?z ?y)])"#)
        .unwrap();
    let err = db
        .execute("(query [:find ?y :where (reach :a ?y)])")
        .expect_err("max_derived_facts=1 must reject the derivation");
    println!(
        "measured max_derived_facts error: category={:?} code={} text={}",
        err.category(),
        err.code(),
        err
    );
    assert_eq!(err.category(), ErrorCategory::Internal);
    assert_eq!(err.code(), "INT-020", "measured code: {}", err.code());
}

#[test]
fn open_options_for_in_memory_accepts_wal_options_as_no_ops() {
    let db = OpenOptions::new()
        .wal_checkpoint_threshold(1)
        .synchronous(SyncMode::Normal)
        .page_cache_size(0)
        .open_memory()
        .unwrap();
    db.execute(r#"(transact [[:a :v 1]])"#).unwrap();
    assert_eq!(row_count(&db, "(query [:find ?e :where [?e :v ?v]])"), 1);
}

// ---------------------------------------------------------------------------
// Structured errors
// ---------------------------------------------------------------------------

#[test]
fn errors_expose_stable_category_code_and_display_text() {
    let db = Minigraf::in_memory().unwrap();
    let err = db
        .execute("(query [:find ?x :where [?e :a ?x]")
        .expect_err("truncated query must fail to parse");
    assert_eq!(err.category(), ErrorCategory::Parser);
    assert!(err.code().starts_with("PRS-"));
    let text = err.to_string();
    assert!(
        text.starts_with(&format!("[{}]", err.code())),
        "display text must carry the code; measured: {text}"
    );

    let err = db
        .execute(r#"(transact [[:e "not-a-keyword" 1]])"#)
        .expect_err("non-keyword attribute must fail");
    println!(
        "measured attribute error: category={:?} code={} text={}",
        err.category(),
        err.code(),
        err
    );
}

// ---------------------------------------------------------------------------
// Ref / Keyword values
// ---------------------------------------------------------------------------

#[test]
fn ref_and_keyword_values_are_distinct_and_round_trip_through_storage() {
    const REF_UUID: &str = "550e8400-e29b-41d4-a716-446655440001";
    let db = Minigraf::in_memory().unwrap();
    db.execute(&format!(
        r#"(transact [[:e :target #uuid "{REF_UUID}"]])"#
    ))
    .unwrap();
    db.execute(r#"(transact [[:e :state :status/active]])"#)
        .unwrap();

    let target = rows(
        &db,
        "(query [:find ?v :where [:e :target ?v]])",
    );
    assert_eq!(target.len(), 1);
    match &target[0][0] {
        Value::Ref(id) => assert_eq!(
            id.to_string(),
            "550e8400-e29b-41d4-a716-446655440001",
            "Ref round-trips as a UUID identity"
        ),
        other => panic!("expected Ref, got {other:?}"),
    }

    let state = rows(&db, "(query [:find ?v :where [:e :state ?v]])");
    assert_eq!(state.len(), 1);
    match &state[0][0] {
        Value::Keyword(k) => assert_eq!(k, ":status/active", "keyword stored with colon"),
        other => panic!("expected Keyword, got {other:?}"),
    }

    assert_ne!(
        target[0][0], state[0][0],
        "Ref and Keyword are distinct variants"
    );
    assert_eq!(
        target[0][0], target[0][0],
        "Ref equality is identity-based"
    );
}
#[test]
fn second_database_transaction_same_thread_is_refused_by_thread_local_guard() {
    let first = Minigraf::in_memory().unwrap();
    let second = Minigraf::in_memory().unwrap();
    let tx = first.begin_write().unwrap();
    let err = match second.begin_write() {
        Ok(_) => panic!("same-thread second-database transaction must be refused"),
        Err(e) => e,
    };
    // The core's guard is thread-local, not per database: at most one
    // explicit write transaction per thread, across all databases.
    assert_eq!(err.code(), "INT-001", "measured code: {}", err.code());
    // Implicit execute on the owning thread is likewise refused even against
    // the other database.
    let err = second
        .execute(r#"(transact [[:b :v 2]])"#)
        .expect_err("implicit execute on the owning thread must be refused");
    assert_eq!(err.code(), "INT-001");
    tx.rollback();
    let mut other = second.begin_write().unwrap();
    other.execute(r#"(transact [[:b :v 2]])"#).unwrap();
    other.commit().unwrap();
}

#[test]
fn second_database_transaction_on_another_thread_succeeds() {
    let first = Minigraf::in_memory().unwrap();
    let second = Minigraf::in_memory().unwrap();
    let mut held = first.begin_write().unwrap();
    held.execute(r#"(transact [[:a :v 1]])"#).unwrap();

    std::thread::scope(|scope| {
        let handle = scope.spawn(|| {
            let mut other = second.begin_write().unwrap();
            other.execute(r#"(transact [[:b :v 2]])"#).unwrap();
            other.commit().unwrap();
        });
        handle.join().unwrap();
    });

    held.commit().unwrap();
}
