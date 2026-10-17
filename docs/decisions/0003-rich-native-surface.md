# ADR 0003 (draft / M3.2 checkpoint proposal) - Rich native surface

Status: **accepted 2026-10-17** with the three amendments below. Basis:
frozen M3.1 release (`0.1.0-preview.4`, qualified source `418e9fc`),
measured characterization in `docs/evidence/m3.2/characterization.md`
(pinned Minigraf 2.0.7), and the user-approved M3.2 mandate.

## Approval record (2026-10-17)

Approved as drafted with the following required amendments, all folded
into this document:

1. **Wrapper lifetime**: the `MgsWriteTx` opaque wrapper's lifetime is
   independent of the consuming Rust `WriteTransaction`. `commit` and
   `rollback` terminalize (consume) the inner transaction; only
   `mgs_write_tx_free` releases the wrapper, so post-commit/rollback
   failure diagnostics remain retrievable from the wrapper.
2. **Thread affinity** is enforced at both the shim and managed
   boundaries, including cleanup/disposal paths. No arbitrary
   finalizer-thread teardown of a thread-affine native transaction is
   permitted (no managed finalizers; cross-thread terminalization fails
   deterministically instead of touching the transaction).
3. **Options-open failures** return structured errors through an owned
   `error_json_out` parameter, because no database handle exists on open
   failure. Stale-error clearing semantics are defined for every
   structured-error path.

Approved gates: rich surface stays on `MinigrafPreparedDatabase`;
typed Ref/Keyword enrichment approved with full M3.1 requalification;
closed options subset approved; `Synchronous` claim bounded (accept/reject
only, no durability claim); general gate-6 error model approved.
Progress past M3.2b requires no further approval unless the
thread-affinity cleanup requirement forces a material ABI/ownership
redesign.

## Scope

Include, in independently qualified slices:

1. explicit write transactions;
2. typed database open options (subset of the pinned core's `OpenOptions`);
3. structured native error information;
4. typed Ref and Keyword result values;
5. runtime native/core identity exposure;
6. qualification and packaging of the above.

Prepare/prepared-query behavior from M3.1 is frozen except where this ADR
explicitly records an approved change (typed decode, see §5).

## Native capability basis (measured)

- Core transactions: `begin_write()` borrows the DB and holds the engine
  write mutex; writes are staged; reads see staged + committed; commit
  consumes and applies WAL-first with one `tx_count`; rollback consumes
  and is infallible; drop = implicit rollback; same-thread reentrancy is
  `INT-001`; another thread's writer blocks until the transaction ends;
  rules register immediately (not transactional); prepared queries see
  committed facts only inside an open transaction.
- Commit failure: error returned, nothing applied, database usable; an
  induced WAL failure surfaced as `INT-000` (coarse classification).
- Open options: `wal_checkpoint_threshold` (default 1000; `usize::MAX`
  disables implicit checkpoints), `page_cache_size` (no observable
  behavior), `max_derived_facts` / `max_results` (default 1_000_000;
  enforcement reports `INT-020`), `allow_unlocked` (not exercisable on
  qualified hosts), `synchronous` (`Full`/`Normal`, durability difference
  only under OS crash/power loss).
- Errors: `MinigrafError` = category (Parser/Query/Storage/Wal/Api/
  Internal) + documented stable text code (`PRS-`/`QRY-`/`STG-`/`WAL-`/
  `API-`/`INT-`, returned by public `code()`) + message; Display is
  `[CODE] message`.
- Values: core `Value::Ref(Uuid)` and `Value::Keyword(String with ':')`
  are distinct; the official C layer flattens both to JSON strings.
- Identity: the core and minigraf-c expose no runtime version function;
  the build-composed shim identity token is the only runtime identity.

## ABI additions (owned `mgs_` surface; closed set)

Existing M3.1 inventory (12 exports) stays unchanged. Additions:

```c
// Open options: options_json is a closed object; absent fields = defaults.
// On failure the function returns NULL and, when error_json_out is non-NULL,
// stores an owned structured-error JSON string there (NULL on success).
// The caller frees it with mgs_string_free. No db handle exists on failure,
// so this out-parameter is the only channel (amendment 3).
MgsDb *mgs_open_with_options(const char *path, const char *options_json,
                             char **error_json_out);
MgsDb *mgs_open_in_memory_with_options(const char *options_json,
                                       char **error_json_out);

// Explicit write transactions (db-owned; engine write lock held for the
// transaction lifetime, exactly as the core does).
MgsWriteTx *mgs_begin_write(MgsDb *db);
char       *mgs_tx_execute(MgsWriteTx *tx, const char *datalog);
int         mgs_tx_commit(MgsWriteTx *tx);    // 0 = committed, -1 = error
void        mgs_tx_rollback(MgsWriteTx *tx);  // infallible, terminal
int         mgs_write_tx_free(MgsWriteTx *tx); // 0 = freed; -1 = refused
// mgs_write_tx_free: if the inner transaction is still open it is rolled
// back and released first, but only on the owner thread. A wrong-thread
// call while open returns -1 and leaves the wrapper alive (diagnostic via
// mgs_tx_last_error_json) - never a silent cross-thread teardown
// (amendment 2). Terminalized wrappers may be freed from any thread.

// Structured error info for the last failure on a handle, as owned JSON:
// {"category":"...","code":"...","message":"..."}
// NULL when there is no error. Freed with mgs_string_free.
char *mgs_last_error_json(MgsDb *db);
char *mgs_tx_last_error_json(MgsWriteTx *tx);
```

### Structured-error semantics (all paths)

- An operation's error state is written on failure and cleared on success:
  after any successful operation on the handle (execute, prepare,
  prepared execute, tx execute, commit), the corresponding
  `*_last_error_json` returns NULL. Opening also clears/sets the
  out-parameter: NULL is stored on success, an owned JSON string on
  failure.
- A failed operation replaces the stored error; terminal-state misuse is a
  failure and replaces it.
- `mgs_tx_last_error_json` remains valid and readable after the inner
  transaction was consumed by commit or rollback, until the wrapper is
  freed (amendment 1). This is how a failed commit's category/code/message
  survive terminalization.
- Shim-level failures (not produced by the core) use category `Shim` with
  stable MinigrafSharp-owned codes: `MGS-001` thread-affinity violation,
  `MGS-002` use of a terminal transaction, `MGS-003` invalid argument
  (null handle/string). `message` always preserves the human diagnostic.
- Native categories/codes are copied from the measured `MinigrafError`
  (Parser/Query/Storage/Wal/Api/Internal and the documented code string);
  unknown/unavailable structured data maps to category `Unknown` with the
  diagnostic retained.

ABI rules: every export has explicit ownership (owned JSON strings freed
with `mgs_string_free`; borrowed identity string unchanged), explicit
lifetime (transaction freed by commit/rollback/free exactly once), a
deterministic error contract (NULL/-1 plus last-error JSON, never a
silent default), documented nullability (all pointers non-null except
returns), and documented threading (one transaction per database at a
time, engine-serialized; no cross-thread transaction use).

Not added: `page_cache_size`/`allow_unlocked` options (no qualified
meaning on the qualified set / not exercisable), checkpoint on the shim
surface, cursor/fact-log/v3 APIs, any raw Rust type.

## Managed ownership and state machine

Public additions (names reviewed here):

- `MinigrafOpenOptions` (immutable): `Default`; `WalCheckpointThreshold`
  (`ulong?`, null = engine default 1000; `Unlimited` constant maps to
  `usize::MAX`), `MaxDerivedFacts`, `MaxResults` (`ulong?`), `Synchronous`
  (`MinigrafSyncMode { Full, Normal }`). Validation happens before any
  native call; unknown/out-of-range values throw `ArgumentException`.
- `MinigrafWriteTransaction : IDisposable`:
  `ExecuteRaw(string)`, `Execute(string)`, `Commit()`, `Rollback()`,
  `Dispose()`.
- `MinigrafNativeErrorCategory` enum: Parser, Query, Storage, Wal, Api,
  Internal, Unknown.
- `MinigrafException` (or derived) gains `NativeCategory`, `NativeCode`
  (nullable), preserving the existing message text and hierarchy.
- `MinigrafRuntimeIdentity` (read-only): `PackageVersion`,
  `ShimAbiVersion`, `ShimSourceRevision`, `CoreVersion`, `CoreCommit`.
- `MinigrafValueKind` gains `Ref`, `Keyword`; `MinigrafValue` gains
  `GetRef()` (Guid) and `GetKeyword()` (string).
- Where the surface lives: add `Open(string, MinigrafOpenOptions)`,
  `OpenMemory(MinigrafOpenOptions)`, and `BeginWrite()` to
  `MinigrafPreparedDatabase` (the owned-shim database). Renaming that
  type is not possible without breaking M3.1; a separate duplicate
  database type is rejected (bifurcation). The class documentation will
  identify it as the owned native surface (prepared queries + rich
  surface). Alternative (new `MinigrafNativeDatabase` name with
  `MinigrafPreparedDatabase` retained as an alias) is an open question.

Transaction state machine (`Open -> Committed | RolledBack | Disposed`):

- `Commit()`: transitions to Committed on success. On failure, the native
  transaction is already gone (measured commit consumption); the managed
  object transitions to Disposed, releases the native handle, and throws
  `MinigrafExecutionException` carrying the structured error. Terminal
  states never return to Open.
- `Rollback()`: infallible (measured); transitions to RolledBack.
- `Dispose()` on an Open transaction performs rollback then free (measured
  native drop = rollback); never commits implicitly. Double dispose is
  harmless. Use after any terminal state throws
  `ObjectDisposedException` deterministically.
- The database owns live transactions: Dispose of the database rolls back
  and frees all registered open transactions before releasing the native
  db (`SafeHandle`), exactly the M3.1 cascade pattern; failed creation
  leaks nothing.

## Gate and threading contract (revised for amendment 2)

The native engine holds the write mutex for the transaction's whole
lifetime, so writers on one database are serialized by the engine itself.
The managed per-database gate is **not** held across the transaction
lifetime. Instead:

- `BeginWrite` takes the gate only to check that no transaction is open,
  call `mgs_begin_write`, and register the child. The core's
  write-transaction guard is **thread-local, not per database** (measured
  during M3.2b): at most one explicit transaction per thread across all
  databases, plus one per database (engine write mutex). Conflicting
  attempts fail deterministically with `InvalidOperationException` rather
  than blocking.
- Transaction operations (`Execute`/`ExecuteRaw`/`Commit`/`Rollback`/
  `Dispose`) never acquire the database gate before the native call. They
  hold only the transaction's own lock. This is required for
  deadlock-freedom: a concurrent `db.Execute` may hold the gate while
  blocking on the engine write mutex, and the transaction owner must still
  be able to commit and release that mutex.
- Terminalization order is native-first: commit/rollback/free the native
  transaction (releasing the engine lock), then take the gate to
  unregister the child. `db.Dispose` then cannot wait on a transaction
  whose owner is waiting on the gate.
- Thread affinity is enforced at both boundaries (amendment 2). The shim
  records the owner `ThreadId` at begin and refuses operations from other
  threads (category `Shim`, code `MGS-001`); `mgs_write_tx_free` refuses
  to tear down an open transaction from a non-owner thread (returns -1,
  wrapper alive). The managed type records the owning managed thread id
  and throws `InvalidOperationException` before any native call,
  including `Dispose`. There are no finalizers anywhere on this path, so
  no GC/finalizer thread can tear down a thread-affine transaction.
- `db.Dispose` with a registered open transaction requires the disposing
  thread to be the transaction's owner; otherwise it fails deterministically
  (`InvalidOperationException`) before touching any native state. No
  partial teardown, no cross-thread rollback.
- Separate databases remain fully independent (no shared gate). Cross-
  library same-file rules are unchanged (M3.1 invariants 1/2).

The managed transaction's public terminal semantics are the Rust ones:
`Commit` consumes, `Rollback` consumes and is infallible,
`Dispose` on an open transaction rolls back then frees (never commits).
A terminalized wrapper is freed immediately by the managed layer after
capturing any failure diagnostics; `MgsWriteTx` wrapper retention exists
to make those diagnostics safe at the ABI boundary, not as a managed
leak.

## Error model

- Preserve the native category and documented code verbatim; never map
  unknown conditions onto a known category. Missing/absent structured
  info maps to `MinigrafNativeErrorCategory.Unknown` with the diagnostic
  text retained (commit I/O failure measured as `INT-000` shows why the
  unknown path must exist).
- Existing behavior is a superset: messages keep the native text; the
  M3.1 cases (`INT-033`, `INT-035`, `PRS-018`, refusal text) remain
  observable. No behavior may key off message text (M3.1 invariant 7
  continues: classification comes from the structured fields, message is
  diagnostics).
- Codes are exposed as strings because the core documents them as stable
  reference codes; no numeric constants are invented.

## Typed Ref/Keyword values

- The shim result serializer emits, for these two variants only, typed
  JSON objects instead of flattened strings:
  `{"type":"ref","value":"<uuid>"}` and
  `{"type":"keyword","value":":<text>"}`. Other value shapes stay
  byte-identical to the official binding.
- The managed decoder adds `Ref`/`Keyword` kinds for these objects. The
  official `MinigrafDatabase` path keeps its measured flattened-string
  behavior (the official C layer loses the distinction); this asymmetry
  is documented as a capability of the owned surface, not a regression.
- Compatibility decision (explicit): results on the shim-backed surface
  change for Ref/Keyword values from String to typed kinds. This is an
  intentional, approved enrichment of the preview surface. It requires
  rerunning the M3.1 native/shim/prepared harnesses and managed/package
  qualification; the change is called out in the evidence and CHANGELOG.
- Round trip: typed decode plus the existing typed bind path
  (`MinigrafBindValue.ValRef`/`ValKeyword`) must be proven end to end.
  The M3.1 bindings schema is extended only if the typed decode surfaces
  a gap; any extension means ADR 0002 section update plus the full
  prepared requalification.

## Open options mapping (closed)

| Managed | Native | Default | Qualification |
| --- | --- | --- | --- |
| `WalCheckpointThreshold` | `OpenOptions::wal_checkpoint_threshold` | 1000; `null` = default; `Unlimited` = `usize::MAX` | commit/close WAL presence |
| `MaxDerivedFacts` | `OpenOptions::max_derived_facts` | 1_000_000 | INT-020 enforcement |
| `MaxResults` | `OpenOptions::max_results` | 1_000_000 | INT-020 enforcement |
| `Synchronous` | `SyncMode::{Full,Normal}` | Full | enable/disable accepted; durability difference not claimed (no OS-crash qualification this milestone) |

`MinigrafDatabase.Open`/`OpenMemory` behavior is unchanged (defaults).
Excluded with recorded reasons: `page_cache_size`, `allow_unlocked`.
Invalid forms (negative/out-of-range, unknown enum, malformed JSON) fail
before any database use.

## Identity policy

- Expose only stable concepts: managed package version, shim ABI version,
  shim source revision, core version, core commit. Binary hashes remain
  qualification/provenance data, not runtime API.
- The existing shim identity verification is unchanged and remains the
  loader gate; `MinigrafRuntimeIdentity` reads the same token (plus the
  managed assembly version) after the verified load.
- Negative control: a deliberately mismatched native combination must be
  refused before use, must not cache a handle, and a subsequent valid
  load must succeed (extending the M3.1 refusal evidence).

## Compatibility and invariants

- M3.1 invariants 1-7 remain in force for the extended surface: no
  cross-library same-file concurrency, sequential handoff preserved,
  cascade ownership, idempotent disposal, no cross-library fallback,
  positive identity verification, no message-parsing for behavior.
- Prepared query semantics stay frozen: prepared execution inside an open
  transaction sees committed facts only (measured native behavior, now
  documented as a contract); rules inside a transaction are not
  transactional (measured) and are documented as such.
- Unqualified RIDs stay unqualified; no new RIDs.
- Package inventory gains no new files (binaries change; all per-RID
  hashes and provenance regenerate through the pipeline; no hand-edited
  artifact hashes).

## Explicit exclusions

Nested/ambient/distributed transactions; automatic retry; implicit
transaction scopes; schema/ORM/LINQ/change-tracking/pooling; new query
features; checkpoint on the shim; v3 APIs; performance work beyond the
new surface; publication (separate dispatch).

## Gates (resolved 2026-10-17)

1. Public managed types above and placement on `MinigrafPreparedDatabase`
   — approved.
2. ABI additions as amended (12 existing + 9 new exports = 21), with the
   wrapper lifetime, thread-affinity enforcement and `error_json_out`
   amendments — approved; header regenerated and hashed per slice.
3. Transaction disposal = rollback (measured native basis) and the
   revised thread-affinity/gate contract — approved.
4. Typed Ref/Keyword decode change on the shim surface with full M3.1
   requalification — approved.
5. Closed options subset and the `Unlimited` sentinel representation —
   approved.
6. Error surface: `NativeCategory` + `NativeCode` on the existing
   exception hierarchy, `Shim`/`Unknown` categories, mandatory unknown
   path and the stale-error clearing semantics — approved.

Evidence: `docs/evidence/m3.2/` per slice (characterization, transactions,
open-options, errors, values, identity, qualification, review), plus the
existing `docs/verification.md` M3.1 regression record.
## Amendment for M3.2d ABI review - accepted 2026-10-17

Structured-error completion (M3.2d) inventoried every fallible owned-shim
path (`docs/evidence/m3.2/errors.md`). All paths either already expose
the closed `{"category","code","message"}` JSON or degrade explicitly to
`Unknown` (classic opens have no handle; managed loader refusals have no
native error object; managed prechecks are not native failures).

One hard gap requires a 23rd export: prepared-query handles store
structured error state, but only database and transaction handles have a
`*_last_error_json` accessor. Proposed addition:

```c
char *mgs_prepared_last_error_json(const MgsPreparedQuery *prepared);
```

Contract: identical to `mgs_prepared_last_error` except the result is an
owned JSON string (same closed shape; caller frees with
`mgs_string_free`), NULL when no error, cleared by the next successful
prepared operation, failures replace earlier ones, never a channel for a
NULL handle. Ownership/lifetime are unchanged (same borrowed handle).

Rejected alternatives (recorded in the evidence inventory): dual-writing
to the parent database handle (dangling `MgsDb*` after `mgs_close` while
a prepared query remains valid), a polymorphic `void*` accessor
(type-unsafe), and moving prepared errors onto the database (breaks the
M3.1 handle-local contract).

Also proposed for acceptance with this amendment (no ABI impact):

- Strict unknown rule: absent/malformed/unknown-category metadata yields
  `NativeCategory = Unknown` and `NativeCode = null`; a known category
  with no code yields `NativeCode = null`; codes are never inferred from
  message text, and a code presented with an unknown category is not
  exposed publicly.
- `docs/error-reference.md` becomes the stable shim-error registry
  (`MGS-001`, `MGS-002`, `MGS-003`, `MGS-004`) plus the core
  category/code mapping and a link to the pinned core error reference.
- A pure managed parser entry point (no mutable state, never throws) is
  added for defensive testing of malformed metadata.

Approved as proposed: exactly one new export (mgs_prepared_last_error_json), the strict unknown rule, the MGS registry, and the pure internal parser test entry point. ABI inventory reconciliation (2026-10-17, during M3.2d): the aggregate count had drifted by one; the true inventory is 12 (M3.1) + 7 (M3.2b) + 2 (M3.2c) + 1 (M3.2d) = **22 exports**, with no ABI content missing. The generated header is authoritative. No further approval gate applies unless implementation requires another ABI addition or a public-surface change.
