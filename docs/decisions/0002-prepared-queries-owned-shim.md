# ADR 0002 draft / M3.1 checkpoint proposal - prepared queries over an owned native shim

Status: accepted 2026-10-17 as the basis of M3.1a/M3.1b/M3.1c after review
rounds; executed and qualified per docs/verification.md (M3.1a/M3.1b/M3.1c
evidence). Doubles as the M3.1 checkpoint proposal.

Revision notes for this draft (review round 1):

- Ordering fixed: the v2.0.7 maintenance/pin-refresh checkpoint completes
  first; this ADR is then revalidated against the exact post-refresh core
  version before any implementation. Pin-related facts below are stated
  against the current `2.0.5` pin and are marked for re-verification;
  no status is carried forward by assumption.
- The shim no longer copies upstream lib.rs. It is the smallest possible
  owned C ABI directly over the pinned core; duplicating the official
  minigraf-c surface is rejected except where a demonstrated technical
  requirement forces a subset (recorded element by element below).
- Hard invariants are explicit and numbered.
- M3.1 stays win-x64-only and provisional; `MinigrafDatabase` is not
  modified.

Review gate: no implementation before this document is approved as
ADR 0002 **and** the maintenance checkpoint below has landed with its
requalification evidence.

## Context

M3.1 is the smallest coherent prepared-query capability. This document
covers the exact native API being wrapped, the managed API shape, statement
ownership and disposal, database/query lifetime relationships, parameter
and value ownership, error behavior, thread and concurrency assumptions,
compatibility constraints, tests, and explicit non-goals.

### Ordering: maintenance checkpoint first (review decision 1)

The maintenance/pin-refresh checkpoint
(`docs/m3-checkpoint-1-proposal.md`, rescoped to maintenance) completes
before any M3.1 implementation. It refreshes the pinned native baseline
from `v2.0.5` to the newest published v2.x pair (`v2.0.7`) with no C ABI
change and requalifies the existing suite. Its effect on this ADR:

1. The shim's exact core target is the post-refresh pinned core version
   (expected `2.0.7`, recorded with commit and checksum once the
   maintenance checkpoint lands). This ADR states its core-API facts
   against the sources verified today (v2.0.5 and v2.0.7 satisfy them;
   both checked below) and is explicitly revalidated against the final
   version before implementation.
2. Upstream issue rechecks are measured, not carried by assumption:
   #554/#555/#558 are re-qualified on the refreshed baseline during the
   maintenance requalification, and #603 is re-qualified through the
   prepared-query surface in this checkpoint (tripwire style). Their
   statuses in the final ADR text come from those measurements.

### Upstream surface audit (observed 2026-10-17)

- Official minigraf-c has releases v2.0.6 and v2.0.7, but their
  `include/minigraf.h` is text-identical to the pinned v2.0.5 header
  (same 7 functions). The `main` branch header is likewise identical
  (same SHA-256 as the archived header: `8603C074…ADE7D`).
- The upstream engine `v3` work exists as a `v3` branch of minigraf-c.
  Its header (SHA-256
  `67C4B6E31056EF36F8741AB9BA48DFA552AA98F9CB52F99B014875D83D6A047F`)
  adds: `open_with_options`, `current_tx_count`, a query cursor API
  (`query`/`cursor_vars`/`cursor_next_batch`/`cursor_last_error`/
  `cursor_free`), a fact-log API, and a log-writer API. It does **not**
  expose prepared statements (`prepare`/bind slots), write transactions,
  or a version/ABI identity function. The v3 engine itself is not yet
  published as a release; no v3 binding binaries exist.
- Core (engine) prepared statements: `src/query/datalog/prepared.rs` at
  both `v2.0.5` and `v2.0.7` has the same public surface:
  `Minigraf::prepare(query) -> PreparedQuery` and
  `PreparedQuery::execute(bindings: &[(&str, BindValue)])`. The only
  v2.0.5 -> v2.0.7 delta is the #603 fix (v2.0.5 prepared queries drop a
  literal `:as-of`/`:valid-at`/`:any-valid-time` when binding slots;
  v2.0.7 keeps them). Core statuses re-checked against exact source at
  implement time, per the ordering rule above.
- The `minigraf` crate is published on crates.io for every upstream
  version including `2.0.5` and `2.0.7` (checksums verifiable at build
  time), so an owned shim can depend on the exact pinned core without
  any sibling-repo checkout.
- Core publishes structured error codes (`API-010` bind-slot-through-
  execute, `INT-033` missing slot, `INT-034` attribute slot at prepare,
  `INT-035` slot type mismatch) but the C layer only serializes them as
  text; no C error-code API exists at any published version.

### Why an owned extension is required for M3.1

The product spec (`product-spec.md` §10) prefers upstream minigraf-c
exposure, then a managed wrapper, and permits a MinigrafSharp-owned
native extension only when upstream cannot deliver in a useful timeframe
and a real consumer needs it. Current facts: upstream has released
repeatedly but has shipped no prepared-query C surface in any line
(v2.x, `main`, or the v3 branch header); the consumer (user decision)
wants the capability now in parallel with upstream. Therefore this ADR
defines the MinigrafSharp-owned extension. Filing an upstream request
remains a checkpoint step (below), not a blocker.

## Decision summary (revised)

Option B (make the owned shim carry the whole engine and requalify
everything on a mirrored official surface) is rejected as out of scale
and as the kind of duplication review removed. The accepted shape:

- The shim is an **additive second native library** with its own minimal
  typed surface directly over the pinned core. It does not reproduce the
  official minigraf-c API. It exports exactly the functions M3.1 needs,
  under a distinct `mgs_` prefix, in their own binary
  (`minigrafsharp_shim`), loaded through their own loader path with
  positive identity verification.
- The existing `MinigrafDatabase` (official minigraf-c library) is
  untouched; a new, explicitly provisional managed surface
  (`MinigrafPreparedDatabase` / `MinigrafPreparedQuery`) uses the shim.
- The shim avoids even its own reallocation of the official JSON result
  vocabulary: prepared results serialize through the exact JSON shapes
  documented for the official binding (`{"variables":…,"results":…}), so
  the single existing managed decoder stays the contract. The serializer
  is reimplemented minimally in the shim (no lib.rs copy) and its
  byte-identity against the official library is fixture-tested (cross-
  engine parity probes).
- POSIX/other RID builds are out of scope this checkpoint (win-x64 only,
  recorded as pending elsewhere; never marked passed).

When upstream minigraf-c ships prepared statements, or a v3 line is
qualified here, a later checkpoint migrates the provisional surface onto
the upstream library and retires the shim, without changing the managed
API.

## Exact native API being wrapped

### Wrapped core Rust API (pinned core; version recorded post-maintenance)

From `src/query/datalog/prepared.rs` and `src/db.rs` (verified identical
at `v2.0.5` and `v2.0.7` at proposal time; restated from exact source at
the post-refresh version before implementation):

```rust
Minigraf::prepare(datalog: &str) -> Result<PreparedQuery, MinigrafError>
PreparedQuery::execute(
    &self,
    bindings: &[(&str, BindValue)],
) -> Result<QueryResult, MinigrafError>
```

- `PreparedQuery` is an owned struct, *not* borrow-bound to the database
  object: it holds the parsed query template, the collected (sorted,
  deduplicated) slot names, and clones of shared engine state
  (`FactStorage`, `Arc<RwLock<RuleRegistry>>`,
  `Arc<RwLock<FunctionRegistry>>`). Execution reads live engine state;
  it needs no engine pointer at call time.
- `BindValue` variants: `Entity(Uuid)`, `Val(Value)`, `TxCount(u64)`,
  `Timestamp(i64)`, `AnyValidTime`.
- Position rules: `Entity` fills `[$slot :attr ?v]`; `Val` fills
  `[?e :attr $slot]` and expression literals (including rule-invocation
  argument positions); `TxCount`/`Timestamp` fill `:as-of $slot`;
  `Timestamp`/`AnyValidTime` fill `:valid-at $slot`.
- Errors (all text carries the stable code): `INT-034` attribute slot at
  prepare; `INT-033` missing slot; `INT-035` slot type mismatch; plain
  execute with slots is rejected core-side (`API-010`). `prepare` rejects
  `transact`, `retract`, and rules before any storage read.
- Extra bindings not referenced by the query are silently ignored
  (upstream semantics, kept verbatim).
- Result shapes through the shim equal the official binding's shapes
  exactly (see JSON parity note above).

### Owned minimal shim ABI (`mgs_` prefix, one additive library)

Each shim element exists under a demonstrated technical requirement —
there is no other way for a managed caller to reach core engine state —
and the set is closed with respect to M3.1. The shim does not mirror the
official 7-function surface beyond this subset.

Native types (opaque): `MgsDb`, `MgsPreparedQuery`.
Owned strings: freed with `mgs_string_free`. Borrowed strings: copied or
read before any next call on the same handle; never freed by the caller.

```c
// --- database lifecycle (to host engine state; no alternative exists) ---
MgsDb *mgs_open(const char *path);        // NULL on failure (no diagnostic handle exists).
MgsDb *mgs_open_in_memory(void);          // NULL on failure (same honesty as official open).
void   mgs_close(MgsDb *handle);          // exactly once per owned handle.

// --- data path (required: prepare accepts only queries, so facts enter
//     through the same engine's transact path; also serves non-prepared
//     reads on the shim surface) ---
char *mgs_execute(MgsDb *handle, const char *datalog);
// Owned result JSON, byte-identical shapes to the official binding.
// NULL on error.

// --- diagnostics (error-copy discipline; failed opens have no handle,
//     matching the recorded official-library behavior) ---
const char *mgs_last_error(const MgsDb *handle);      // borrowed.
const char *mgs_prepared_last_error(
    const MgsPreparedQuery *prepared);                // borrowed.

// --- memory discipline ---
void   mgs_string_free(char *s);                      // owned shim strings; NULL safe.

// --- prepared-query additions (the M3.1 capability itself) ---
MgsPreparedQuery *mgs_prepare(MgsDb *handle, const char *datalog);
 // NULL on error (diagnostic on the same db handle).
char *mgs_prepared_slot_names(const MgsPreparedQuery *prepared);
 // Owned JSON array of sorted, deduplicated slot names.
char *mgs_prepared_execute(MgsPreparedQuery *prepared, const char *bindings_json);
 // bindings_json: array of {"name","bind":{"kind","value"}} objects
 // (kind table in Parameter ownership). Owned result JSON or NULL on error
 // (diagnostic via mgs_prepared_last_error).
void   mgs_prepared_query_free(MgsPreparedQuery *prepared); // once; NULL safe.

// --- load-time identity probe (hard invariant 6) ---
const char *mgs_shim_identity(void);
 // Static, process-lifetime borrowed string, immutable per binary:
 // {"shim ABI version", "core version", "core commit", "source revision"}.
 // Read once at load; never freed; no allocation involved.
```

Not exported (and why): `checkpoint` (no managed prepared-cleanup
requirement this checkpoint; file-backed prepared databases rely on WAL
replay at open plus documented `Dispose` durability; exposed later only
with justification), the official surface's remaining functions
(`string_free`-mirror naming is replaced by `mgs_string_free`; there is
no second `last_error` API family; nothing else of the official mirror
exists), options opening, cursors, fact logs, writers (v3-only ideas out
of scope).

Provenance pins for the shim (recorded under a new `shim` key in
`native/upstream.json`): crates.io `minigraf` at the post-refresh pinned
version with Cargo.lock checksums; upstream core commit; the shim ABI
version (`0.1.0`) with its own revision counter; compiler/toolchain
versions per RID at build time; per-binary SHA-256. No upstream shell
source files are copied: every shim source file is owned by
`native/shim/`.

## Managed API shape

Provisional and additive; the spec sketch (`product-spec.md` §10) is
followed for naming. New public types (namespace `MinigrafSharp`;
`MinigrafDatabase` untouched):

```csharp
public sealed class MinigrafPreparedDatabase : IDisposable
{
    public static MinigrafPreparedDatabase Open(string path);
    public static MinigrafPreparedDatabase OpenMemory();
    public string ExecuteRaw(string datalog);   // shim mgs_execute
    public MinigrafResult Execute(string datalog);
    public MinigrafPreparedQuery Prepare(string datalog);
    public void Dispose();
}

public sealed class MinigrafPreparedQuery : IDisposable
{
    public IReadOnlyList<string> SlotNames { get; }  // from mgs_prepared_slot_names, sorted
    public MinigrafResult Execute(
        IReadOnlyDictionary<string, MinigrafBindValue> bindings);
    public void Dispose();
}

public readonly struct MinigrafBindValue
{
    public static MinigrafBindValue Entity(Guid entity);
    public static MinigrafBindValue Val(MinigrafValue value);   // String/Integer/Float/Boolean/Null
    public static MinigrafBindValue ValRef(Guid entity);        // explicit ref identity for rule args
    public static MinigrafBindValue ValKeyword(string keyword); // explicit keyword identity
    public static MinigrafBindValue Transaction(ulong txCount); // `:as-of $slot`
    public static MinigrafBindValue Timestamp(long unixMillis); // `:as-of`/`:valid-at`
    public static MinigrafBindValue AnyValidTime();
}
```

- No `Checkpoint` on the shim surface this checkpoint (explicit non-goal
  above). `ExecuteRaw`/`Execute` remain: without the shim's own data
  path there is no way to write facts; they also cover reads/retract
  flows on the shim surface. This is the demonstrated technical
  requirement, not a convenience: prepared queries only accept query
  templates.
- `MinigrafPreparedQuery.Execute` returns `MinigrafResult` through the
  existing `MinigrafResultDecoder` (no second decoder; identical result
  semantics). No new value kinds: prepared results arrive with the same
  Ref/Keyword flattening as the official execute path.
- `MinigrafBindValue` is a discriminated struct with the same immutable,
  type-sensitive conventions as `MinigrafValue`; managed serialization
  to bindings_json is total (no caller-controlled JSON fragments).

## Statement/query ownership and disposal

- `MinigrafPreparedQuery` owns exactly one native `MgsPreparedQuery*`.
  Its Dispose runs `mgs_prepared_query_free` exactly once; it
  participates in the parent's cascade below; disposal is idempotent.
- The managed query object registers itself with its parent database
  (add/remove under the database gate). The parent keeps the registration
  list so it can cascade-close registered, still-open queries even if
  their managed object was collected without explicit disposal; no
  finalizer or SafeHandle is introduced for the query pointer. After
  cascade or after its own Dispose, further use of the query throws
  `ObjectDisposedException`.
- The database and the query each hold borrowed-error synchronization
  under the same per-database gate; child error reads happen under the
  gate.

## Database/query lifetime relationships

- The query keeps a managed reference to its parent database, so the
  parent cannot be fully disposed before every live query is closed
  (hard invariant 3).
- Native-side, the shim `PreparedQuery` holds shared-engine `Arc`s, so
  closing the native engine does not free query state; the deterministic
  ordering is nevertheless enforced managed-side: parent Dispose first
  cascade-closes every registered open child query, then releases the
  native db through the SafeHandle.
- Reusing the database after closing queries is unchanged behavior;
  preparing again is allowed.

## Parameter/value ownership

- Managed bind values own their payloads. Serialization to
  bindings_json is UTF-8 through `LibraryImport`; each `kind` maps
  exactly one managed bind kind:
  - `entity` <- `Entity(Guid)` (canonical UUID text);
  - `val` <- `Val(MinigrafValue)` with `type`/`value` payload per
    `MinigrafValueKind` (including `null`);
  - `ref` <- `ValRef(Guid)`;
  - `keyword` <- `ValKeyword(string)`;
  - `tx_count` <- `Transaction(ulong)`;
  - `timestamp` <- `Timestamp(long)`;
  - `any_valid_time` <- `AnyValidTime()` (value absent).
- Non-finite float bind values are rejected by the managed layer before
  serialization (deterministic argument error): they cannot be
  represented losslessly in bindings JSON.
- All strings crossing the shim boundary back into managed memory are
  copied immediately (strict UTF-8 bounds checked) and freed through
  `mgs_string_free` exactly once; no owned or borrowed shim pointer
  outlives a call; no allocator other than the shim's own is used;
  `Marshal.FreeHGlobal`, `FreeCoTaskMem`, `NativeMemory.Free` or CRT
  `free` are forbidden against shim allocations exactly as for the
  official library.

## Error behavior

- Same copy-then-throw discipline as the M0-M2 surface: every shim
  failure copies the borrowed error text under the gate before any
  sibling call, then throws.
- Exception mapping: prepare failures and prepared execute failures map
  to `MinigrafExecutionException` (no new exception type; no
  over-classification); shim data-path failures on
  `MinigrafPreparedDatabase` map likewise. Open failures are honest
  (`MinigrafOpenException` names the requested path and states that no
  native diagnostic exists — the shim keeps the no-handle-on-failed-open
  reality). Malformed native JSON reuses `MinigrafResultException`.
- Managed argument rules (deterministic, checked before native calls):
  null datalog/null bindings dictionary/unknown kind in a binding/null
  name or value entry are `ArgumentException`/`ArgumentNullException`;
  disposed query or database are `ObjectDisposedException`.
- Error codes are never parsed for behavior (hard invariant 7); they may
  appear in messages for diagnostics only.

## Hard invariants (review round 1; each: enforcement, verification)

1. **No concurrent cross-library ownership of the same database file.**
   When a file is owned by either the official or the shim library, the
   other library's open of that file is rejected and observed as such.
   Enforcement: engine file locking only (the managed layer adds no
   global lock or registry). Verification: cross-engine bidirectional
   open-rejection probes recorded in the checkpoint evidence.
2. **Sequential file handoff is qualified.** A file written by one
   surface and closed is openable by the other surface, both directions,
   with WAL replay verified by content checks. Verification: handoff
   probes with byte-content assertions and exact cleanup evidence. A
   failed handoff probe blocks the checkpoint.
3. **Prepared queries cannot outlive their prepared database.**
   Enforcement: managed cascade ordering under the parent gate (dispose
   parent -> close registered children -> release native db); the
   managed query object references its parent, so the parent's disposal
   always happens after children close. Verification: lifecycle tests
   (cascade, post-cascade use rejection) and cleanup checks.
4. **Disposal is deterministic and idempotent** at both database and
   query levels (second Dispose returns without effect; ordering is
   fixed; no GC-ordering dependence is introduced). Verification:
   double-dispose tests in both orders.
5. **No silent fallback between official and shim libraries.** The shim
   loader resolves only the shim binary; if it cannot load it, it throws
   `MinigrafNativeLoadException` with the shim's probed locations. No
   loader path ever substitutes the official library, and the official
   loader never substitutes the shim.
6. **Loader/library identity is positively verified.** After load, the
   loader calls `mgs_shim_identity()` and requires the exact pinned
   identity token (shim ABI version, core version, core commit,
   provenance revision) compiled into this MinigrafSharp revision.
   Symbol presence alone is not accepted as identity. Checkpoints
   include a negative probe: the official binary offered under the shim
   import must be rejected clearly by the identity check (symbol-missing
   or token-mismatch), never silently accepted.
7. **No parsing of textual error messages or error-code strings for
   behavior.** Text is diagnostics only; behavior keys off return values
   (owned pointer/NULL, rc, identity token) and explicit probes.
   Verification: code review gate plus tests asserting deterministic
   exception types route on conditions, not on message contents.

## Thread/concurrency assumptions

- Everything on a prepared database is serialized through one per-
  database gate (the same pattern as `MinigrafDatabase`): `Prepare`,
  child registration/cascade, prepared `Execute`, `ExecuteRaw`,
  and `Dispose`. No managed caller observes parallel native calls on
  one database.
- Rationale: the pinned core line carries upstream cross-thread
  commit/checkpoint visibility issues (#554/#555/#558, fixed upstream in
  the refresh line) plus the single-writer file behavior already
  recorded. Serializing the entire surface sidesteps the race class
  rather than relying on unmeasured engine guarantees. Post-refresh
  rechecks (maintenance checkpoint) are recorded before this ADR is
  accepted; any relaxation of the gate is a later, separately qualified
  checkpoint.
- Two different prepared databases proceed in parallel (independent
  gates). Cross-library same-file behavior is governed by hard
  invariants 1 and 2 only.
- No ambient `System.Transactions`, no async path, no managed thread
  affinity.

## Compatibility constraints

- Black-box file compatibility is unchanged: databases stay format v7;
  hard invariants 1 and 2 govern file relationships between the two
  libraries; no managed or native layer adds extra files, sidecars or
  shadow copies.
- The official minigraf-c library, its staged assets, its version pins,
  `NativeLoader`, and all existing managed behavior are unchanged; the
  shim introduces its own loader with a distinct library file name
  (`minigrafsharp_shim.dll` for win-x64), its own
  `MINIGRAFSHARP_SHIM_NATIVE_PATH` override, the same candidate search
  order, Resolver registration for the second library name, and the
  identity check (invariant 6).
- Packaging: the shim ships as an additional native asset in the same
  package layout (runtimes/<rid>/native; native/staging/<rid>) with its
  own recorded provenance; **win-x64 only in this checkpoint**; other
  RIDs recorded as pending, never passed. Cargo + cbindgen are the
  documented build prereqs; CI notes the shim build job, but the
  checkpoint is locally win-x64 qualified.
- Public API is additive and provisional: no existing member of
  `MinigrafDatabase` or its exceptions changes; no existing meaning is
  reinterpreted. The shim ABI itself is versioned (`0.1.0`) and tagged in
  provenance; identity tokens make binary/managed pairing explicit.
- `native/upstream.json` gains the shim provenance block; official-
  library header and asset hash checks stay unchanged.

## Tests

Test programs follow the existing conventions (plain `Program.cs`
harness projects, no xunit). The matrix below targets coverage; exact
case names are fixed in the checkpoint record reviewed with this ADR.

- Identity / load gate:
  - shim loads once; identity token matches the pinned expectation;
    second load is the same handle (process-singleton).
  - negative probe: the official binary offered under the shim import
    (override env var pointing at the official asset) fails to load
    through the explicit identity/symbol check, with the shim-specific
    load-failure signature.
- Positive (Tests / IntegrationTests roots):
  - prepare once; `SlotNames` equals the exact sorted name set; repeated
    `Execute` with different bindings returns fresh-state semantics.
  - transact then prepared query sees the new state; prepared query at
    `Transaction(1)` returns exactly the tx-1 view; `Timestamp` and
    `AnyValidTime` windows behave per upstream-derived fixtures.
  - expression slot `(>= ?v $threshold)`; combined entity+as-of+valid-at+
    keyword; rule-invocation literal via `ValRef`; `Val` of `null`.
  - extra bindings silently ignored (upstream semantics preserved).
  - cross-engine parity: identical Datalog through `MinigrafDatabase`
    and `MinigrafPreparedDatabase` produce byte-identical result JSON
    (`ExecuteRaw` comparison on the same fixture).
- Negative (all deterministic managed failure; no undefined behavior):
  - prepare rejects `transact`, `retract`, rule use, attribute slot.
  - missing required slot; type mismatch per position table; malformed
    bindings JSON; non-finite float; wrong payload kind for a
    `MinigrafValue` (managed prechecks), null keys/values in the
    bindings dictionary; null/empty datalog.
- Lifecycle:
  - prepare -> N executes -> Dispose query; db reuse afterwards;
    prepare-after-dispose rejection; double dispose (query and db,
    either order); parent cascade of open queries with post-cascade
    rejection (hard invariants 3 and 4).
  - same-file sequential handoff between official and shim databases in
    both directions, with WAL replay content checks (hard invariant 2);
    concurrent cross-engine open of the same file rejected both
    directions without crashing either handle (hard invariant 1).
  - reopened file-backed prepared database (Dispose then re-Open)
    replays WAL and answers prepared queries correctly.
  - #603 recheck via the shim on the post-refresh core: prepared queries
    with a literal `:as-of`/`:valid-at`/`:any-valid-time` next to slots
    keep those literals. On the pre-refresh core this probe records the
    still-broken behavior as a tripwire that trips when behavior
    changes; on the post-refresh core it asserts the fixed behavior with
    byte-agreement counting against unprepared queries (both_ways).
  - #554/#555/#558 recheck results from the maintenance requalification
    are cited in the M3.1 evidence record; no M3.1 probe claims their
    fix through the serialized managed surface (they are not reachable
    through it; measured facts only, per hard invariant 7).
- Evidence: one durable `docs/evidence/` artifact bound to the exact
  source revision, host, RID, toolchain and cleanup state per
  `remediation-evidence-standard.md`; the qualification target
  statement (`Minigraf <post-refresh version> / minigraf-c <version> /
  shim ABI 0.1.0 with provenance revision`) printed by the harness gate
  like the M1d probes.

## Explicit non-goals

- No write transactions, open options, structured-error C API, typed
  Ref/Keyword result decoding, ABI-version surface beyond the shim
  identity probe, checkpoint surface on the shim path, cursors or fact
  logs (v3-only ideas not in scope).
- No attaching prepared queries to `MinigrafDatabase` instances created
  through the official library: a foreign library's engine state is
  unreachable; recorded as a `not reachable` fact, not a pending gap.
- No reliance on, or qualification of, the v3 engine or its C branch.
- No managed fallback routes between the two libraries (invariant 5):
  failure is explicit, never silent.
- No new exception taxonomy; no `MinigrafDatabase` modification; no
  platform expansion beyond win-x64; no publish/packaging change beyond
  the additive native asset.
- No Gagamba or sibling-repo modifications; no upstream source files
  copied into the shim (dependencies pinned via crates.io with
  checksums); no new third-party dependencies beyond the pinned engine
  crate, its pinned transitive dependencies, and the JSON/uuid crates.
- `docs/v2-qualification.md` M1d content stays untouched by M3.1; new
  evidence goes to separate files.

## Risks and open review questions

1. Two engine copies in one process (memory footprint doubled; file
   locking behavior across copies is the same code and is observed, not
   assumed - covered by hard-invariant probes 1 and 2).
2. Managed-surface bifurcation: two database types temporarily; the
   explicitly accepted provisional trade, migration path upstream.
3. The user-visible caveat: prepare is available only on
   `MinigrafPreparedDatabase` (routing rule made explicit). The ADR
   records it as `not reachable` by design.
4. Review-round open questions resolved here or by round 2:
   - bindings_json kind table exactly as in Parameter ownership;
   - `Checkpoint` deliberately absent on the shim path this checkpoint
     (review may add it with justification);
   - identity token content (shim ABI version + core version + core
     commit + provenance revision) and its bump policy.

## Filing the upstream request

A prepared-query C exposure request is filed upstream (minigraf-c issue,
flagged against the v3 line as well), including this audit's evidence
and the shim ABI draft (changed per review: the minimal owned `mgs_`
surface, not a mirror proposal). Filing happens during this checkpoint
under the user-approved external-action rule; the shim remains the
working surface until upstream lands the C API and a recorded migration
checkpoint retires it.

## Review gate

No implementation starts before this document is approved as ADR 0002
(after round-2 review) and after the maintenance checkpoint's
requalification evidence lands and is cited here with exact versions and
probe results. Approver gates: (a) the minimal owned ABI element list,
(b) ownership/cascade design, (c) hard invariants 1-7 and their
probes, (d) bindings schema and identity token policy, (e) the
maintenance-first ordering (v2.0.7 refresh completes first; ADR
revalidated against its exact post-refresh core version; upstream issue
statuses rechecked by measurement, not assumption).
