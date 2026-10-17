# M3.2a - Native capability characterization (Minigraf 2.0.7)

Status: executed 2026-10-17. Host: win-x64, rustc/cargo 1.99.1. Sources:
crates.io `minigraf 2.0.7` (tarball SHA-256
`74E6724A59FECE46F0F56C3FE16F1792B34E34FE08FDFF4C902B22696D760D68`,
1443572 bytes, unpacked under the local cargo registry) and the pinned
minigraf-c 2.0.7 reference `official-c-207-lib.rs` (SHA-256
`B14CA2F167B989309310A5052B76E30E9072A6534FD5A22A1DF52F767E8B65B9`,
10174 bytes). File/line references below are to the pinned crate source.

Measured probes: `native/shim/tests/characterization.rs`, 16 tests, run by
`cargo test --locked` (and therefore inside every `dist` job on all three
qualified RIDs). Local win-x64 result: 16/16 pass; measured diagnostics
quoted verbatim below.

## 1. Transaction creation and ownership

- `Minigraf::begin_write() -> Result<WriteTransaction<'_>, MinigrafError>`
  (`src/db.rs:681`). The transaction borrows the database (`'a`) and holds
  the engine write mutex (`MutexGuard<'a, WriteContext>`) for its whole
  lifetime (`src/db.rs:1098-1110`).
- The Rust lifetime makes "transaction outlives database" unrepresentable
  natively; the managed layer must reproduce that ordering with cascade
  ownership (M3.1 discipline).
- Same-thread reentrancy is refused: a thread-local `WRITE_TX_ACTIVE` flag
  makes a second `begin_write()` and an implicit `db.execute()` return
  `INT-001` (`src/db.rs:47-59`, `685-690`, `560-563`). Measured:
  `[INT-001] a WriteTransaction is already in progress on this thread`.
- `WriteTransaction::execute(&mut self, input)` stages writes and applies
  rules immediately:
  - transact/retract are buffered in memory and return `QueryResult::Ok`
    (not `Transacted`/`Retracted`) (`src/db.rs:1128-1141`; measured);
  - reads see committed facts **plus** staged facts (read-your-own-writes)
    (`merged_query_facts`, `src/db.rs:1281`);
  - rules are written to the shared rule registry immediately, so a rule
    defined inside a later-rolled-back transaction persists (measured:
    `tx_rule_registration_is_not_transactional`).
- Prepared queries hold shared engine state, not the database handle. A
  prepared query executed while a write transaction is open sees only
  committed facts (staged facts have not reached `FactStorage`); a
  transactional Datalog read sees both (measured:
  `tx_prepared_query_executes_but_does_not_see_staged_facts`).

## 2. Commit

- `commit(self)` consumes the transaction (`src/db.rs:1216`). All staged
  facts are stamped with one `tx_count` and one `tx_id`; the WAL entry is
  written and fsynced before any fact is applied to the shared
  `FactStorage` (`commit_inner`, `src/db.rs:1220-1278`).
- One commit is one `tx_count`, regardless of how many staged batches went
  in (measured). An empty commit succeeds and does not bump `tx_count`
  (measured).
- On WAL failure nothing is applied and the handle's pending state is
  discarded by `Drop` (implicit rollback) since `committed` was never set;
  the database remains usable for a later transaction (measured: forced
  WAL-path failure, then a successful commit after removing the blocker).
- Measured failed-commit diagnostic:
  `category=Internal code=INT-000 text=[INT-000] unclassified internal
  error: Access is denied. (os error 5)` — i.e. a commit I/O failure is
  wrapped as an unclassified internal error, not `WAL-*`/`STG-*`. This is
  a measured limitation of the error taxonomy for commit failures (no
  reproducible way found to induce a typed WAL error at this level).
- Commit cannot be called twice or after rollback at the Rust level (both
  consume `self`); the managed/ABI layer must define deterministic
  terminal-state errors.

## 3. Rollback and drop

- `rollback(self)` is infallible, clears pending facts, releases the lock
  (`src/db.rs:1309-1314`). Measured: staged facts absent afterwards, the
  database immediately usable for a new transaction.
- Dropping without commit performs an implicit rollback
  (`impl Drop`, `src/db.rs:1344-1356`; measured). This is the native basis
  for a fail-safe managed `Dispose` (no implicit commit).

## 4. Database close and failure interaction

- While a transaction is alive the database cannot be dropped (Rust
  borrow); there is no native "close with open transaction" state to
  characterize from Rust. The owned ABI must enforce cascade ordering
  managed-side, exactly as M3.1 does for prepared queries.
- Failed `tx.execute` (parse/materialize error) leaves the transaction
  usable and earlier staged facts intact; a later commit includes them
  (measured: malformed command → `PRS-*`, then a second staging and a
  successful commit with both valid facts).
- No path was found where a failed operation poisons the transaction;
  errors are returned before state changes, and the transaction keeps its
  lock until commit/rollback/drop.

## 5. Concurrency

- The write mutex serializes writers on one database. Measured: a second
  thread's `begin_write()` blocks until the first transaction commits
  (observed > 250 ms of blocking), then proceeds.
- Implicit `db.execute()` from another thread also waits on the same
  mutex, so writes are serialized, not parallel (source: `execute_inner`
  takes the write lock; `src/db.rs:590-600`).
- Same-thread mixed use is refused (`INT-001`), as above.
- Independent databases are independent (each has its own `Inner`), which
  is the basis of the existing M3.1 per-database gate design.
- Cross-process file locking is unchanged from the M3.1a evidence.

## 6. Open options

`OpenOptions` (`src/db.rs:102-236`) is `#[non_exhaustive]` with six fields:

| Option | Type | Default | Meaning | Observable? |
| --- | --- | --- | --- | --- |
| `wal_checkpoint_threshold` | `usize` | `1000` | WAL entries before auto-checkpoint; `usize::MAX` disables every implicit checkpoint including close-time | Yes (WAL file presence) |
| `page_cache_size` | `usize` | `256` | LRU pages for file-backed DBs; ignored in memory | No external observable |
| `max_derived_facts` | `usize` | `1_000_000` | Per-iteration derivation cap | Yes (error) |
| `max_results` | `usize` | `1_000_000` | Per-query derivation cap (DB facts exempt) | Yes (error) |
| `allow_unlocked` | `bool` | `false` | Open without filesystem locking when locking is unavailable | Not exercisable on qualified hosts |
| `synchronous` | `SyncMode` | `Full` | `Full` = fdatasync per WAL write; `Normal` = no per-write flush | Durability difference only under OS crash/power loss; not exercised this slice |

Measured:
- defaults are the pre-option behavior (`Minigraf::open` uses
  `OpenOptions::default()`, `src/db.rs:395-397`); a default open
  checkpoints on clean close (no WAL left);
- `wal_checkpoint_threshold(1)` removes the WAL after each commit;
  `usize::MAX` leaves the WAL in place even after the handle is dropped;
- `max_results(1)` / `max_derived_facts(1)` reject a 2-row derivation
  with `category=Internal code=INT-020` (measured text: `evaluator:
  iteration or result limit exceeded: ...`) — the classification is
  Internal, not Query;
- in-memory opens accept WAL/page-cache options as documented no-ops.

## 7. Errors

- `MinigrafError { category: ErrorCategory, code: &'static str, message:
  String }` (`src/error.rs:1534-1560`). `Display` is `[CODE] message`.
- `ErrorCategory` is `#[non_exhaustive]`: Parser (`PRS-`), Query (`QRY-`),
  Storage (`STG-`), Wal (`WAL-`), Api (`API-`), Internal (`INT-`).
- Codes are documented in the crate's `docs/ERROR_REFERENCE.md` and
  returned by the public `code()` accessor; the crate documents them as
  the stable reference codes (186 codes in 2.0.7). They are not numeric
  ABI constants, but they are a declared, documented textual contract of
  the pinned core.
- Measured examples: `PRS-*` parse failures (display carries the code);
  `API-003` (`attribute must be a keyword`) for a non-keyword attribute;
  `INT-001` reentrancy; `INT-020` limit enforcement; `INT-000` for the
  induced WAL failure.
- The official minigraf-c 2.0.7 layer serializes only the `Display` text;
  it exposes no category or code (`official-c-207-lib.rs` error helpers),
  so structured error metadata is only reachable through the owned shim.

## 8. Ref and Keyword values

- Core `Value` variants: `Ref(EntityId)` where `EntityId = Uuid`, and
  `Keyword(String)` stored with its leading `:` (`src/graph/types.rs:43`,
  `65-84`; measured round trip through storage: Ref keeps its UUID
  identity, Keyword keeps `:status/active`).
- Ref and Keyword are distinct variants with distinct ordering/hash
  behavior; Ref equality is UUID identity, Keyword equality is exact text
  (no case normalization).
- **The official C layer flattens both to JSON strings**
  (`value_to_json`: `Value::Ref(u) => J::String(u.to_string())`,
  `Value::Keyword(k) => J::String(k.clone())`,
  `official-c-207-lib.rs:189-200`). Typed decode is therefore only
  possible on the shim-backed surface with a shim-owned result serializer;
  the official `MinigrafDatabase` path cannot produce typed Ref/Keyword
  without losing information at this layer.

## 9. Identity / version

- The pinned core exposes no runtime version or commit function; the
  crate version exists only in `Cargo.toml` (`2.0.7`). minigraf-c 2.0.7
  likewise exposes no version function.
- The only runtime identity today is the M3.1 shim identity token
  (`mgs_shim_identity`: shim ABI 0.1.0, source revision 1, core 2.0.7,
  core commit `2d2e5d75...`), composed at build time from
  `native/shim/identity.json` and the pinned `native/upstream.json`.

## 10. Probe inventory (all pass on win-x64, 2026-10-17)

| Probe | Measured fact |
| --- | --- |
| `tx_write_returns_ok_and_reads_see_staged_and_committed_facts` | Ok ack; read-your-own-writes; one tx_count per commit |
| `tx_multiple_writes_commit_atomically_as_one_tx_count` | one commit = one tx_count |
| `tx_rollback_discards_staged_facts_and_leaves_db_usable` | rollback infallible; db reusable |
| `tx_drop_without_commit_is_implicit_rollback` | drop = rollback |
| `tx_empty_commit_succeeds_without_tx_count_bump` | empty commit ok, no bump |
| `tx_failed_write_leaves_transaction_usable_and_staged_state_intact` | failed staging keeps tx usable and prior staged facts |
| `tx_same_thread_reentrancy_is_int001` | INT-001 for second begin and implicit execute |
| `tx_second_thread_blocks_until_commit` | other-thread begin blocks until commit |
| `tx_rule_registration_is_not_transactional` | rules persist across rollback |
| `tx_prepared_query_executes_but_does_not_see_staged_facts` | prepared sees committed only; tx read sees both |
| `tx_failed_commit_leaves_clean_state_and_handle_reusable` | INT-000 on induced WAL failure; nothing applied; later commit ok |
| `open_options_default_matches_open_and_threshold_controls_checkpoint` | threshold=1 removes WAL; MAX keeps it; default checkpoints on close |
| `open_options_limits_are_enforced_by_the_engine` | INT-020 for both limits |
| `open_options_for_in_memory_accepts_wal_options_as_no_ops` | WAL options are no-ops in memory |
| `errors_expose_stable_category_code_and_display_text` | category/code/display measured (PRS-*, API-003) |
| `ref_and_keyword_values_are_distinct_and_round_trip_through_storage` | typed Ref/Keyword identity round trip in core |

## 11. Recorded limitations / open questions for ADR 0003

- `SyncMode::Normal` durability is documented core behavior but was not
  crash-tested here; exposing it publicly needs a qualification plan
  (fault injection) or explicit non-claim wording.
- `allow_unlocked` is not exercisable on the qualified hosts; no exposure
  is recommended this milestone.
- `page_cache_size` has no externally observable behavior; no public
  option is recommended (native pass-through without qualified meaning
  would violate the no-fake-semantics rule).
- Commit-failure classification is coarse (`INT-000`); the error surface
  must keep the diagnostic text and an explicit unknown/unclassified path
  rather than guessing a category.
- Typed Ref/Keyword decoding changes results on the shim-backed surface;
  ADR 0003 must decide the compatibility treatment and the M3.1
  requalification consequence explicitly.
- The engine has no runtime version API; core identity must come from the
  build-composed shim identity (complemented by managed package identity).

## 12. Environment and cleanup

- Host: win-x64 (`x86_64-pc-windows-msvc`), rustc 1.99.1
  (`58cf8f9c3 2026-10-13`), cargo 1.99.1 (`5f94df478 2026-08-27`).
- Probes use per-test directories under the OS temp dir and remove them on
  drop; the forced-WAL-failure probe removes its blocker directory before
  the later commit. No fixture survives a passing run.
- Cross-RID re-measurement: the probes run inside every `dist` job for
  win-x64, linux-x64 and osx-arm64; the CI run for this slice is recorded
  in the slice commit/report (M3.1a/b/c evidence conventions).

## 13. Cross-RID re-measurement (CI)

CI run https://github.com/jenolaszlo-sketch/minigraf-sharp/actions/runs/39044852111
on revision `59677f6` (all 11 jobs green):
`test result: ok. 16 passed; 0 failed` on each `dist` runner —
win-x64 (2026-10-17T15:29:20Z), linux-x64 (15:25:56Z) and
osx-arm64 (15:26:33Z). The M3.1 suites, packaging, tamper matrix and
NativeAOT consumers stayed green, so M3.2a is regression-free and the
characterization facts above are measured on all three qualified RIDs.
## 14. Addendum during M3.2b: the write guard is thread-local

Implementation probes exposed one contract the original probe set missed
(now covered by two additional Rust probes, 18 total):
`WRITE_TX_ACTIVE` is thread-local, not per database. Measured:
`second_database_transaction_same_thread_is_refused_by_thread_local_guard`
(second database `begin_write` on the owning thread → `INT-001`; implicit
`execute` on that thread is refused even against the other database) and
`second_database_transaction_on_another_thread_succeeds` (a different
thread can hold a transaction on another database concurrently). The
managed layer mirrors this thread-local contract; per-database engine
serialization is unchanged.
