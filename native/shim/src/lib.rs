//! MinigrafSharp-owned C ABI shim over the pinned `minigraf` core crate
//! (crates.io `minigraf = "=2.0.7"`, provenance in `native/upstream.json`):
//! prepared queries (M3.1) and explicit write transactions (M3.2b).
//!
//! Contracts: docs/m3-1a-shim-abi-proposal.md (ADR 0002) and
//! docs/decisions/0003-rich-native-surface.md (ADR 0003).
//! Identity: `mgs_shim_identity()`; token composed by build.rs from
//! `identity.json` and `native/upstream.json`. No upstream file is copied.

#![deny(unsafe_op_in_unsafe_fn)]
#![allow(clippy::not_unsafe_ptr_arg_deref)]

use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use minigraf::{BindValue, Minigraf, PreparedQuery, QueryResult, Value, WriteTransaction};
use serde_json::Value as Json;

// ─── Handles ──────────────────────────────────────────────────────────────────

/// Shared per-database engine cell; the shim is single-writer per database at
/// the native layer, exactly like the official binding. Owned `PreparedQuery`
/// execution shares this gate (the prepared query carries its own shared-state
/// clones and never touches the `Minigraf` object itself).
struct EngineCell {
    db: Mutex<Minigraf>,
}

/// Structured error state for one handle. `text` backs the borrowed
/// `mgs_last_error`/`mgs_prepared_last_error` accessors (legacy contract);
/// `category`/`code`/`message` back the owned `*_last_error_json` accessors.
/// An operation's error state is written on failure and cleared on success.
struct ErrorState {
    text: CString,
    category: &'static str,
    code: String,
    message: String,
}

type ErrorCell = Mutex<Option<ErrorState>>;

pub struct MgsDb {
    engine: Arc<EngineCell>,
    last_error: ErrorCell,
    /// Number of live `MgsWriteTx` wrappers whose inner Rust transaction is
    /// still open. `mgs_close` refuses to free the engine (leaks it instead)
    /// when this is non-zero, so no borrowed transaction can dangle.
    open_transactions: AtomicUsize,
}

pub struct MgsPreparedQuery {
    prepared: PreparedQuery,
    query_text: String,
    engine: Arc<EngineCell>,
    last_error: ErrorCell,
}

/// Opaque explicit write-transaction wrapper (M3.2b). The wrapper outlives
/// the consuming Rust `WriteTransaction`: `commit`/`rollback` terminalize the
/// inner transaction and leave the wrapper alive so failure diagnostics stay
/// readable until `mgs_write_tx_free` (ADR 0003 amendment 1).
pub struct MgsWriteTx {
    /// Thread that created the transaction; enforced for every operation
    /// including cleanup (ADR 0003 amendment 2).
    owner: std::thread::ThreadId,
    /// Database used only to decrement `open_transactions` when the inner
    /// transaction is terminalized. Never dereferenced once `inner` is None.
    db: *mut MgsDb,
    inner: Option<WriteTransaction<'static>>,
    last_error: ErrorCell,
}

fn make_cstring(text: &str) -> CString {
    CString::new(text).unwrap_or_else(|_| CString::new("error").unwrap_or_default())
}

fn set_error(cell: &ErrorCell, category: &'static str, code: String, text: String) {
    if let Ok(mut guard) = cell.lock() {
        *guard = Some(ErrorState {
            text: make_cstring(&text),
            category,
            code,
            message: text,
        });
    }
}

fn category_name(category: minigraf::ErrorCategory) -> &'static str {
    match category {
        minigraf::ErrorCategory::Parser => "Parser",
        minigraf::ErrorCategory::Query => "Query",
        minigraf::ErrorCategory::Storage => "Storage",
        minigraf::ErrorCategory::Wal => "Wal",
        minigraf::ErrorCategory::Api => "Api",
        minigraf::ErrorCategory::Internal => "Internal",
        // `ErrorCategory` is `#[non_exhaustive]`; never guess for a future
        // variant: unknown stays unknown (ADR 0003 error model).
        _ => "Unknown",
    }
}

/// Shim-owned failure codes (ADR 0003): MGS-001 thread-affinity violation,
/// MGS-002 terminal-transaction misuse, MGS-003 invalid argument,
/// MGS-004 internal shim failure (poisoned lock, non-representable text).
fn store_shim_error(cell: &ErrorCell, code: &str, msg: &str) {
    set_error(cell, "Shim", code.to_string(), msg.to_string());
}

fn store_core_error(cell: &ErrorCell, e: &minigraf::MinigrafError) {
    set_error(
        cell,
        category_name(e.category()),
        e.code().to_string(),
        format!("{e:#}"),
    );
}

fn clear_error(cell: &ErrorCell) {
    if let Ok(mut guard) = cell.lock() {
        *guard = None;
    }
}

fn error_text(cell: &ErrorCell) -> *const c_char {
    match cell.lock() {
        Ok(guard) => match guard.as_ref() {
            Some(s) => s.text.as_ptr(),
            None => std::ptr::null(),
        },
        Err(_) => std::ptr::null(),
    }
}

fn error_json(cell: &ErrorCell) -> *mut c_char {
    let body = match cell.lock() {
        Ok(guard) => match guard.as_ref() {
            Some(state) => serde_json::json!({
                "category": state.category,
                "code": state.code,
                "message": state.message,
            })
            .to_string(),
            None => return std::ptr::null_mut(),
        },
        Err(_) => return std::ptr::null_mut(),
    };
    match CString::new(body) {
        Ok(s) => s.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

fn to_owned_json_or_error(json: String, err: &ErrorCell) -> *mut c_char {
    match CString::new(json) {
        Ok(s) => s.into_raw(),
        Err(_) => {
            store_shim_error(err, "MGS-004", "result JSON contained a null byte");
            std::ptr::null_mut()
        }
    }
}

// ─── Identity ─────────────────────────────────────────────────────────────────

/// Static, NUL-terminated identity token; composed at build time by build.rs
/// from `identity.json` and `native/upstream.json`. Byte-exact fixed key order:
/// `{"shim_abi":…,"shim_source_revision":…,"core_version":…,"core_commit":…}`.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_shim_identity() -> *const c_char {
    static TOKEN: &[u8] = concat!(env!("MGS_SHIM_IDENTITY_TOKEN"), "\0").as_bytes();
    TOKEN.as_ptr().cast()
}

// ─── Lifecycle ────────────────────────────────────────────────────────────────

/// Open a file-backed Minigraf database. NULL on failure (no diagnostic handle).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_open(path: *const c_char) -> *mut MgsDb {
    if path.is_null() {
        return std::ptr::null_mut();
    }
    let Ok(path) = unsafe { CStr::from_ptr(path) }.to_str() else {
        return std::ptr::null_mut();
    };
    match Minigraf::open(path) {
        Ok(db) => Box::into_raw(Box::new(MgsDb {
            engine: Arc::new(EngineCell {
                db: Mutex::new(db),
            }),
            last_error: Mutex::new(None),
            open_transactions: AtomicUsize::new(0),
        })),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Open an in-memory Minigraf database. NULL on failure (no diagnostic handle).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_open_in_memory() -> *mut MgsDb {
    match Minigraf::in_memory() {
        Ok(db) => Box::into_raw(Box::new(MgsDb {
            engine: Arc::new(EngineCell {
                db: Mutex::new(db),
            }),
            last_error: Mutex::new(None),
            open_transactions: AtomicUsize::new(0),
        })),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Close a database and free all associated memory; exactly once per owned
/// handle; NULL safe.
///
/// Defensive contract: if a write transaction is still open on this handle,
/// the engine is intentionally leaked instead of freed, so the transaction's
/// borrowed engine state can never dangle (managed ownership cascades first;
/// this only guards native misuse).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_close(handle: *mut MgsDb) {
    if !handle.is_null() {
        let open = unsafe { &*handle }.open_transactions.load(Ordering::SeqCst);
        if open > 0 {
            return;
        }
        unsafe { drop(Box::from_raw(handle)) };
    }
}

/// Free an owned string from `mgs_execute`, `mgs_prepared_execute` or
/// `mgs_prepared_slot_names`; NULL safe.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_string_free(s: *mut c_char) {
    if !s.is_null() {
        unsafe { drop(CString::from_raw(s)) };
    }
}

// ─── Last error ───────────────────────────────────────────────────────────────

/// Last error on the database handle (borrowed; valid until the next call on
/// the same handle). NULL when no error has occurred.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_last_error(handle: *const MgsDb) -> *const c_char {
    if handle.is_null() {
        return std::ptr::null();
    }
    let handle = unsafe { &*handle };
    error_text(&handle.last_error)
}

/// Last error on the prepared-query handle (borrowed; valid until the next call
/// on the same handle). NULL when no error has occurred.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_prepared_last_error(prepared: *const MgsPreparedQuery) -> *const c_char {
    if prepared.is_null() {
        return std::ptr::null();
    }
    let prepared = unsafe { &*prepared };
    error_text(&prepared.last_error)
}

// ─── Execute ──────────────────────────────────────────────────────────────────

/// Execute a Datalog string against the database. Owned JSON result (official
/// shapes) or NULL on error (diagnostic via `mgs_last_error`).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_execute(handle: *mut MgsDb, datalog: *const c_char) -> *mut c_char {
    if handle.is_null() || datalog.is_null() {
        return std::ptr::null_mut();
    }
    let handle = unsafe { &*handle };
    let datalog = match unsafe { CStr::from_ptr(datalog) }.to_str() {
        Ok(s) => s,
        Err(_) => {
            store_shim_error(&handle.last_error, "MGS-003", "invalid UTF-8 in datalog string");
            return std::ptr::null_mut();
        }
    };
    let db_guard = match handle.engine.db.lock() {
        Ok(g) => g,
        Err(_) => {
            store_shim_error(&handle.last_error, "MGS-004", "database lock poisoned");
            return std::ptr::null_mut();
        }
    };
    let result = db_guard.execute(datalog);
    drop(db_guard);
    match result {
        Ok(qr) => {
            clear_error(&handle.last_error);
            to_owned_json_or_error(query_result_to_json(qr), &handle.last_error)
        }
        Err(e) => {
            store_core_error(&handle.last_error, &e);
            std::ptr::null_mut()
        }
    }
}

// ─── Prepare ──────────────────────────────────────────────────────────────────

/// Prepare a `(query …)` Datalog string. NULL on error; the diagnostic is
/// stored on the database handle (transact/retract/rule are not preparable;
/// attribute slots are rejected at prepare; parse failures per core).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_prepare(
    handle: *mut MgsDb,
    datalog: *const c_char,
) -> *mut MgsPreparedQuery {
    if handle.is_null() || datalog.is_null() {
        return std::ptr::null_mut();
    }
    let handle = unsafe { &*handle };
    let datalog = match unsafe { CStr::from_ptr(datalog) }.to_str() {
        Ok(s) => s,
        Err(_) => {
            store_shim_error(&handle.last_error, "MGS-003", "invalid UTF-8 in datalog string");
            return std::ptr::null_mut();
        }
    };
    let db_guard = match handle.engine.db.lock() {
        Ok(g) => g,
        Err(_) => {
            store_shim_error(&handle.last_error, "MGS-004", "database lock poisoned");
            return std::ptr::null_mut();
        }
    };
    let result = db_guard.prepare(datalog);
    drop(db_guard);
    match result {
        Ok(pq) => {
            clear_error(&handle.last_error);
            Box::into_raw(Box::new(MgsPreparedQuery {
                prepared: pq,
                query_text: datalog.to_string(),
                engine: Arc::clone(&handle.engine),
                last_error: Mutex::new(None),
            }))
        }
        Err(e) => {
            store_core_error(&handle.last_error, &e);
            std::ptr::null_mut()
        }
    }
}

/// Sorted, deduplicated list of bind slot names, as a JSON string array.
/// Mirror of the core's private slot collection on the token level (the core
/// keeps its parsed list private); see docs/m3-1a-shim-abi-proposal.md.
/// Owned string; NULL on scan failure (diagnostic via `mgs_prepared_last_error`).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_prepared_slot_names(prepared: *const MgsPreparedQuery) -> *mut c_char {
    if prepared.is_null() {
        return std::ptr::null_mut();
    }
    let prepared = unsafe { &*prepared };
    match prepared.slot_names() {
        Ok(names) => {
            let json = serde_json::to_string(&names).unwrap_or_else(|_| "[]".to_string());
            match CString::new(json) {
                Ok(s) => s.into_raw(),
                Err(_) => {
                    store_shim_error(
                        &prepared.last_error,
                        "MGS-004",
                        "slot-names JSON contained a null byte",
                    );
                    std::ptr::null_mut()
                }
            }
        }
        Err(msg) => {
            store_shim_error(&prepared.last_error, "MGS-004", &msg);
            std::ptr::null_mut()
        }
    }
}

/// Execute the prepared query with bindings from JSON. Owned JSON result
/// (official shapes) or NULL on error (diagnostic via `mgs_prepared_last_error`).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_prepared_execute(
    prepared: *mut MgsPreparedQuery,
    bindings_json: *const c_char,
) -> *mut c_char {
    if prepared.is_null() || bindings_json.is_null() {
        return std::ptr::null_mut();
    }
    let prepared = unsafe { &*prepared };
    let bindings_json = match unsafe { CStr::from_ptr(bindings_json) }.to_str() {
        Ok(s) => s,
        Err(_) => {
            store_shim_error(&prepared.last_error, "MGS-003", "invalid UTF-8 in bindings JSON");
            return std::ptr::null_mut();
        }
    };
    let owned: Vec<(String, BindValue)> = match parse_bindings(bindings_json) {
        Ok(b) => b,
        Err(msg) => {
            store_shim_error(
                &prepared.last_error,
                "MGS-003",
                &format!("invalid bindings JSON: {msg}"),
            );
            return std::ptr::null_mut();
        }
    };
    let refs: Vec<(&str, BindValue)> =
        owned.iter().map(|(n, v)| (n.as_str(), v.clone())).collect();
    let _engine_guard = match prepared.engine.db.lock() {
        Ok(g) => Some(g),
        Err(_) => {
            store_shim_error(&prepared.last_error, "MGS-004", "database lock poisoned");
            return std::ptr::null_mut();
        }
    };
    match prepared.prepared.execute(&refs) {
        Ok(qr) => {
            clear_error(&prepared.last_error);
            to_owned_json_or_error(query_result_to_json(qr), &prepared.last_error)
        }
        Err(e) => {
            store_core_error(&prepared.last_error, &e);
            std::ptr::null_mut()
        }
    }
}

/// Free a prepared query; exactly once per owned handle; NULL safe. The
/// database handle remains valid and reusable afterward.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_prepared_query_free(prepared: *mut MgsPreparedQuery) {
    if !prepared.is_null() {
        unsafe { drop(Box::from_raw(prepared)) };
    }
}

/// Structured last error on the database handle as owned JSON
/// (`{"category","code","message"}`), or NULL when there is no error.
/// Cleared by the next successful operation on the handle.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_last_error_json(handle: *const MgsDb) -> *mut c_char {
    if handle.is_null() {
        return std::ptr::null_mut();
    }
    error_json(&unsafe { &*handle }.last_error)
}

/// Structured last error on a transaction wrapper (owned JSON) or NULL when
/// there is no error. Remains readable after commit/rollback terminalized the
/// inner transaction, until `mgs_write_tx_free` (ADR 0003 amendment 1).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_tx_last_error_json(tx: *const MgsWriteTx) -> *mut c_char {
    if tx.is_null() {
        return std::ptr::null_mut();
    }
    error_json(&unsafe { &*tx }.last_error)
}

// --- Explicit write transactions (M3.2b; ADR 0003) ---------------------------

fn tx_owner_ok(tx: &MgsWriteTx) -> bool {
    tx.owner == std::thread::current().id()
}

/// Decrement the database's open-transaction count exactly once, when the
/// borrowed Rust transaction is consumed (commit/rollback/free-while-open).
/// Only called while the database is guaranteed alive by that same count.
fn tx_release_open_count(tx: &MgsWriteTx) {
    if !tx.db.is_null() {
        let db = unsafe { &*tx.db };
        db.open_transactions.fetch_sub(1, Ordering::SeqCst);
    }
}

/// Begin an explicit write transaction. NULL on failure; the diagnostic is on
/// the database handle (`mgs_last_error_json`). The engine write mutex is
/// held for the transaction's lifetime, exactly as in the pinned core.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_begin_write(handle: *mut MgsDb) -> *mut MgsWriteTx {
    if handle.is_null() {
        return std::ptr::null_mut();
    }
    let handle_ref = unsafe { &*handle };
    let guard = match handle_ref.engine.db.lock() {
        Ok(g) => g,
        Err(_) => {
            store_shim_error(&handle_ref.last_error, "MGS-004", "database lock poisoned");
            return std::ptr::null_mut();
        }
    };
    let tx = match guard.begin_write() {
        Ok(t) => t,
        Err(e) => {
            store_core_error(&handle_ref.last_error, &e);
            return std::ptr::null_mut();
        }
    };
    // The wrapper outlives the borrow; soundness is guaranteed by ownership:
    // the managed layer cascade-closes transactions before the database, and
    // `mgs_close` leaks rather than freeing while `open_transactions > 0`.
    let tx_static: WriteTransaction<'static> = unsafe { std::mem::transmute(tx) };
    drop(guard);
    handle_ref.open_transactions.fetch_add(1, Ordering::SeqCst);
    clear_error(&handle_ref.last_error);
    Box::into_raw(Box::new(MgsWriteTx {
        owner: std::thread::current().id(),
        db: handle,
        inner: Some(tx_static),
        last_error: Mutex::new(None),
    }))
}

/// Execute a Datalog command inside the transaction. Owned JSON result (same
/// shapes as `mgs_execute`; staged writes acknowledge with `{"ok":true}`) or
/// NULL on error (diagnostic via `mgs_tx_last_error_json`). Thread-affine:
/// a call from any other thread is refused with `Shim/MGS-001`.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_tx_execute(tx: *mut MgsWriteTx, datalog: *const c_char) -> *mut c_char {
    if tx.is_null() {
        return std::ptr::null_mut();
    }
    let tx = unsafe { &mut *tx };
    if datalog.is_null() {
        store_shim_error(&tx.last_error, "MGS-003", "null datalog pointer");
        return std::ptr::null_mut();
    }
    if !tx_owner_ok(tx) {
        store_shim_error(
            &tx.last_error,
            "MGS-001",
            "write transactions are thread-affine; execute from the thread that began the transaction",
        );
        return std::ptr::null_mut();
    }
    let datalog = match unsafe { CStr::from_ptr(datalog) }.to_str() {
        Ok(s) => s,
        Err(_) => {
            store_shim_error(&tx.last_error, "MGS-003", "invalid UTF-8 in datalog string");
            return std::ptr::null_mut();
        }
    };
    let Some(inner) = tx.inner.as_mut() else {
        store_shim_error(
            &tx.last_error,
            "MGS-002",
            "write transaction is already terminal (committed or rolled back)",
        );
        return std::ptr::null_mut();
    };
    match inner.execute(datalog) {
        Ok(qr) => {
            clear_error(&tx.last_error);
            to_owned_json_or_error(query_result_to_json(qr), &tx.last_error)
        }
        Err(e) => {
            store_core_error(&tx.last_error, &e);
            std::ptr::null_mut()
        }
    }
}

/// Commit the transaction. Returns 0 on success, -1 on failure; the wrapper
/// remains alive either way, and a failure is readable via
/// `mgs_tx_last_error_json` until `mgs_write_tx_free` (amendment 1).
#[unsafe(no_mangle)]
pub extern "C" fn mgs_tx_commit(tx: *mut MgsWriteTx) -> std::os::raw::c_int {
    if tx.is_null() {
        return -1;
    }
    let tx = unsafe { &mut *tx };
    if !tx_owner_ok(tx) {
        store_shim_error(
            &tx.last_error,
            "MGS-001",
            "write transactions are thread-affine; commit from the thread that began the transaction",
        );
        return -1;
    }
    let Some(inner) = tx.inner.take() else {
        store_shim_error(
            &tx.last_error,
            "MGS-002",
            "write transaction is already terminal (committed or rolled back)",
        );
        return -1;
    };
    match inner.commit() {
        Ok(()) => {
            tx_release_open_count(tx);
            clear_error(&tx.last_error);
            0
        }
        Err(e) => {
            // The core consumes the transaction on failure; nothing is
            // applied and the database remains usable. Keep the diagnostic on
            // the wrapper.
            tx_release_open_count(tx);
            store_core_error(&tx.last_error, &e);
            -1
        }
    }
}

/// Roll back the transaction (infallible, terminal). A call after the
/// transaction is terminal records `MGS-002`; a wrong-thread call records
/// `MGS-001` and does not touch the native transaction.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_tx_rollback(tx: *mut MgsWriteTx) {
    if tx.is_null() {
        return;
    }
    let tx = unsafe { &mut *tx };
    if !tx_owner_ok(tx) {
        store_shim_error(
            &tx.last_error,
            "MGS-001",
            "write transactions are thread-affine; roll back from the thread that began the transaction",
        );
        return;
    }
    let Some(inner) = tx.inner.take() else {
        store_shim_error(
            &tx.last_error,
            "MGS-002",
            "write transaction is already terminal (committed or rolled back)",
        );
        return;
    };
    inner.rollback();
    tx_release_open_count(tx);
    clear_error(&tx.last_error);
}

/// Free the transaction wrapper; NULL safe semantics return -1 for NULL.
///
/// If the inner transaction is still open it is rolled back first, but only
/// on the owner thread: a wrong-thread call returns -1 with `MGS-001` and
/// leaves the wrapper alive (amendment 2: no cross-thread teardown).
/// Terminalized wrappers may be freed from any thread.
#[unsafe(no_mangle)]
pub extern "C" fn mgs_write_tx_free(tx: *mut MgsWriteTx) -> std::os::raw::c_int {
    if tx.is_null() {
        return -1;
    }
    let tx = unsafe { &mut *tx };
    if let Some(inner) = tx.inner.take() {
        if !tx_owner_ok(tx) {
            tx.inner = Some(inner);
            store_shim_error(
                &tx.last_error,
                "MGS-001",
                "write transactions are thread-affine; free from the thread that began the transaction",
            );
            return -1;
        }
        inner.rollback();
        tx_release_open_count(tx);
    }
    unsafe { drop(Box::from_raw(tx)) };
    0
}

// ─── Result JSON (byte-parity with the official minigraf-c shapes) ────────────

fn value_to_json(v: &Value) -> Json {
    match v {
        Value::String(s) => Json::String(s.clone()),
        Value::Integer(i) => serde_json::json!(i),
        Value::Float(f) => serde_json::Number::from_f64(*f)
            .map(Json::Number)
            .unwrap_or(Json::Null),
        Value::Boolean(b) => Json::Bool(*b),
        Value::Ref(u) => Json::String(u.to_string()),
        Value::Keyword(k) => Json::String(k.clone()),
        Value::Null => Json::Null,
    }
}

fn query_result_to_json(result: QueryResult) -> String {
    let val = match result {
        QueryResult::Transacted(tx_id) => serde_json::json!({ "transacted": tx_id }),
        QueryResult::Retracted(tx_id) => serde_json::json!({ "retracted": tx_id }),
        QueryResult::Ok => serde_json::json!({ "ok": true }),
        QueryResult::QueryResults { vars, results } => {
            let rows: Vec<Vec<Json>> = results
                .iter()
                .map(|r| r.iter().map(value_to_json).collect())
                .collect();
            serde_json::json!({ "variables": vars, "results": rows })
        }
    };
    val.to_string()
}

// ─── Bindings JSON parse ──────────────────────────────────────────────────────

/// Shape: `[{"name":"…","bind":{…}}, …]`; strict closed schema per
/// docs/m3-1a-shim-abi-proposal.md; extra properties rejected; failures are
/// shim diagnostics (never a core error code).
fn parse_bindings(text: &str) -> Result<Vec<(String, BindValue)>, String> {
    let root: Json = serde_json::from_str(text).map_err(|e| e.to_string())?;
    let arr = root.as_array().ok_or("top level must be a JSON array")?;
    let mut out = Vec::with_capacity(arr.len());
    for (idx, el) in arr.into_iter().enumerate() {
        let obj = el.as_object().ok_or(format!("element {idx} must be an object"))?;
        let name = obj
            .get("name")
            .and_then(Json::as_str)
            .filter(|s| !s.is_empty())
            .ok_or(format!("element {idx} needs a non-empty string 'name'"))?
            .to_string();
        let bind = obj
            .get("bind")
            .and_then(Json::as_object)
            .ok_or(format!("element {idx} needs an object 'bind'"))?;
        if bind.len() > 3 {
            return Err(format!("element {idx} 'bind' has extra properties"));
        }
        let kind = bind
            .get("kind")
            .and_then(Json::as_str)
            .ok_or(format!("element {idx} 'bind' needs a string 'kind'"))?;
        let value = bind.get("value");
        let value_type = bind.get("type").and_then(Json::as_str);
        let bind_value = match kind {
            "entity" => BindValue::Entity(parse_uuid(value, &name)?),
            "ref" => BindValue::Val(Value::Ref(parse_uuid(value, &name)?)),
            "keyword" => BindValue::Val(Value::Keyword(
                value
                    .and_then(Json::as_str)
                    .ok_or(format!("keyword bind '{name}' needs a string 'value'"))?
                    .to_string(),
            )),
            "val" => BindValue::Val(parse_val(value, value_type, &name)?),
            "tx_count" => BindValue::TxCount(parse_u64(value, &name)?),
            "timestamp" => BindValue::Timestamp(parse_i64(value, &name)?),
            "any_valid_time" => BindValue::AnyValidTime,
            other => return Err(format!("unknown bind kind '{other}'")),
        };
        out.push((name, bind_value));
    }
    Ok(out)
}

fn parse_uuid(value: Option<&Json>, name: &str) -> Result<uuid::Uuid, String> {
    let text = value
        .and_then(Json::as_str)
        .ok_or(format!("bind '{name}' needs a string 'value'"))?;
    uuid::Uuid::parse_str(text).map_err(|e| format!("bind '{name}': invalid UUID: {e}"))
}

fn parse_u64(value: Option<&Json>, name: &str) -> Result<u64, String> {
    value
        .and_then(Json::as_u64)
        .ok_or(format!("tx_count bind '{name}' needs a JSON integer 'value' within u64"))
}

fn parse_i64(value: Option<&Json>, name: &str) -> Result<i64, String> {
    value
        .and_then(Json::as_i64)
        .ok_or(format!("timestamp bind '{name}' needs a JSON integer 'value' within i64"))
}

fn parse_val(value: Option<&Json>, value_type: Option<&str>, name: &str) -> Result<Value, String> {
    match value_type {
        Some("string") => value
            .and_then(Json::as_str)
            .map(|s| Value::String(s.to_string()))
            .ok_or(format!("val bind '{name}' type 'string' needs a string 'value'")),
        Some("integer") => value
            .and_then(Json::as_i64)
            .map(Value::Integer)
            .ok_or(format!("val bind '{name}' type 'integer' needs a JSON integer within i64")),
        Some("float") => {
            let raw = value.ok_or(format!("val bind '{name}' type 'float' needs a number 'value'"))?;
            let n = raw
                .as_f64()
                .ok_or(format!("val bind '{name}' type 'float' needs a number 'value'"))?;
            if !n.is_finite() {
                return Err(format!("val bind '{name}': non-finite float rejected"));
            }
            Ok(Value::Float(n))
        }
        Some("boolean") => value
            .and_then(Json::as_bool)
            .map(Value::Boolean)
            .ok_or(format!("val bind '{name}' type 'boolean' needs a boolean 'value'")),
        Some("null") => Ok(Value::Null),
        _ => Err(format!("val bind '{name}' needs a string 'type' (string|integer|float|boolean|null)")),
    }
}

// ─── Slot-name structural scan ────────────────────────────────────────────────

impl MgsPreparedQuery {
    /// Best-effort mirror of the core's private `collect_slot_names`.
    /// Token-level scan of the query text with the engine lexer's primitives;
    /// every `$name` token outside strings/comments/number/symbol/keyword/tag
    /// contexts is collected (the engine rejects slot tokens in `:find`/`:keys`/
    /// `:in`/`:with` at prepare, so accepted queries cannot contain them there).
    /// Extra slots beyond the engine-collectable positions are possible in
    /// exotic syntaxes and are harmless: the engine ignores bindings it does
    /// not reference. Sorted + deduplicated, joined as a JSON string array.
    fn slot_names(&self) -> Result<Vec<String>, String> {
        Ok(scan_slot_names(&self.query_text))
    }
}

fn is_slot_name_char(c: char) -> bool {
    c.is_alphanumeric() || c == '_' || c == '-'
}

fn is_symbol_char(c: char) -> bool {
    c.is_alphanumeric() || c == '?' || c == '_' || c == '-' || c == '/'
}

fn is_keyword_char(c: char) -> bool {
    c.is_alphanumeric() || c == '/' || c == '-' || c == '_' || c == '?'
}

fn skip_number(src: &[char], mut i: usize) -> usize {
    i += 1;
    let mut dot = false;
    while i < src.len() {
        let n = src[i];
        if n.is_ascii_digit() {
            i += 1;
        } else if n == '.' && !dot {
            dot = true;
            i += 1;
        } else {
            break;
        }
    }
    i
}

/// Skip helpers return the next position after consuming the token class.
fn skip_string(src: &[char], mut i: usize) -> usize {
    i += 1;
    let mut escaped = false;
    while i < src.len() {
        let c = src[i];
        i += 1;
        if escaped {
            escaped = false;
        } else if c == '\\' {
            escaped = true;
        } else if c == '"' {
            return i;
        }
    }
    i
}

fn skip_comment(src: &[char], mut i: usize) -> usize {
    while i < src.len() && src[i] != '\n' {
        i += 1;
    }
    i
}

fn consume_while(src: &[char], mut i: usize, pred: impl Fn(char) -> bool) -> usize {
    while i < src.len() && pred(src[i]) {
        i += 1;
    }
    i
}

/// Token-level scan mirroring the engine lexer (parser.rs). Unknown/unlexable
/// content is skipped, never misinterpreted: `$` handling only runs after the
/// query has already passed the engine's parse (so `$` with an empty name or
/// other broken token classes cannot be present).
fn scan_slot_names(text: &str) -> Vec<String> {
    let src: Vec<char> = text.chars().collect();
    let mut slots: Vec<String> = Vec::new();
    let mut i = 0;
    while i < src.len() {
        let c = src[i];
        match c {
            '"' => i = skip_string(&src, i),
            ';' => i = skip_comment(&src, i),
            '$' => {
                i += 1;
                let start = i;
                i = consume_while(&src, i, is_slot_name_char);
                if i > start {
                    slots.push(src[start..i].iter().collect());
                }
            }
            '0'..='9' => i = skip_number(&src, i),
            '-' => {
                if i + 1 < src.len() && src[i + 1].is_ascii_digit() {
                    i = skip_number(&src, i);
                } else {
                    i = consume_while(&src, i + 1, is_symbol_char);
                }
            }
            _ if c.is_alphabetic() || c == '?' || c == '_' => {
                i = consume_while(&src, i, is_symbol_char)
            }
            ':' => i = consume_while(&src, i + 1, is_keyword_char),
            '#' => i = consume_while(&src, i + 1, |c| c.is_alphanumeric() || c == '-'),
            '<' | '>' | '=' | '+' | '*' | '/' | '!' => {
                i += 1;
                if i < src.len()
                    && src[i] == '='
                    && (c == '<' || c == '>' || c == '!')
                {
                    i += 1;
                }
            }
            _ => i += 1,
        }
    }
    slots.sort();
    slots.dedup();
    slots
}
#[cfg(test)]
mod tests {
    use super::scan_slot_names;

    fn owned(v: &[&str]) -> Vec<String> {
        v.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn debug1() {
        let r = scan_slot_names("\"$x\"");
        println!("debug1 {:?}", r);
        assert_eq!(r, Vec::<String>::new());
    }

    #[test]
    fn debug2() {
        let r = scan_slot_names("[\"$x\"]");
        println!("debug2 {:?}", r);
        assert_eq!(r, Vec::<String>::new());
    }


    #[test]
    fn empty() {
        assert_eq!(scan_slot_names(""), Vec::<String>::new());
    }

    #[test]
    fn simple_slot() {
        assert_eq!(scan_slot_names("$age"), owned(&["age"]));
    }

    #[test]
    fn skips_string_contents() {
        assert_eq!(scan_slot_names("[?e :note \"cost is $5\""), Vec::<String>::new());
    }

    #[test]
    fn skips_comment() {
        assert_eq!(scan_slot_names("(query ; $x\n $rel)"), owned(&["rel"]));
    }

    #[test]
    fn number_and_symbol_swallow() {
        assert_eq!(scan_slot_names("12$foo"), owned(&["foo"]));
        assert_eq!(scan_slot_names("sym$bol"), owned(&["bol"]));
    }
}
