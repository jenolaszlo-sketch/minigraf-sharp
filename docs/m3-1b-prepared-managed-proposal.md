# M3.1b checkpoint proposal - managed prepared-query ownership and wiring

Status: executed per the approved mandate of 2026-10-17; this document is
the checkpoint record fixed before implementation. Basis: ADR 0002
(approved), the M3.1a native anchor commit `480471e` (immutable for this
slice: any native defect found here is fixed by an explicit M3.1a
requalification, never folded silently into this managed slice), and the
user-approved M3.1b mandate (2026-10-17).

## Scope

Expose the qualified owned shim through a safe managed API with
deterministic ownership, loader identity verification, and behavioral
parity, without broadening platform qualification. Objective invariant:

> No managed object may outlive or ambiguously own the native state on
> which it depends.

Second invariant:

> The managed prepared-query path must prove parity with the
> already-measured shim behavior rather than reinterpret it.

## Managed API surface (additive, provisional; `MinigrafDatabase` untouched)

Namespace `MinigrafSharp`, exactly as ADR 0002 sketches:

- `MinigrafPreparedDatabase : IDisposable` — `Open(string)`,
  `OpenMemory()`, `ExecuteRaw(string)`, `Execute(string)`,
  `Prepare(string)`, `Dispose()`. No raw native handle exposure; no
  `Checkpoint` this checkpoint (ADR non-goal).
- `MinigrafPreparedQuery : IDisposable` — `SlotNames`
  (`IReadOnlyList<string>`, captured at prepare), `Execute(
  IReadOnlyDictionary<string, MinigrafBindValue>)`, `Dispose()`.
- `MinigrafBindValue` — closed discriminated struct: `Entity(Guid)`,
  `Val(MinigrafValue)`, `ValRef(Guid)`, `ValKeyword(string)`,
  `Transaction(ulong)`, `Timestamp(long)`, `AnyValidTime()`; each maps
  one shim bindings kind (entity / val / ref / keyword / tx_count /
  timestamp / any_valid_time). Managed serialization to bindings JSON is
  total (no caller-controlled JSON fragments). Non-finite float `Val`
  payloads are rejected deterministically (ArgumentException) at
  construction. Keyword text is passed through as supplied (no
  normalization); text without the leading `:` addresses no engine
  keyword (measured shim semantics kept, no reinterpretation).
- Exceptions: existing taxonomy only. Load/identity failures:
  `MinigrafNativeLoadException`. Prepare/execute/shim data-path
  failures: `MinigrafExecutionException`. Malformed native JSON:
  `MinigrafResultException`. Argument problems:
  `ArgumentNullException`/`ArgumentException`/`ObjectDisposedException`.

## Loader (`MINIGRAFSHARP_SHIM_NATIVE_PATH`)

- One combined assembly-level `SetDllImportResolver` routes
  strictly by library name: `minigraf` to the official loader path,
  `minigrafsharp_shim` to the new internal `ShimNativeLoader`. Unknown
  names fall through unresolved. No fallback between libraries in any
  failure mode (hard invariant 5 of ADR 0002).
- `ShimNativeLoader`: same candidate order as the official loader
  (`MINIGRAFSHARP_SHIM_NATIVE_PATH` override, `runtimes/<rid>/native/`,
  base directory, staging walk), distinct file name
  (`minigrafsharp_shim.dll` / `libminigrafsharp_shim.so` /
  `libminigrafsharp_shim.dylib`), no PATH probing, process-lifetime
  retention, a failed load never caches a handle (same convention as the
  official loader, retry resolves afresh).
- Positive identity verification after load, before the handle is ever
  returned: the loader reads `mgs_shim_identity` directly via
  `GetExport` (no P/Invoke, no resolver recursion), copies the borrowed
  token immediately, and requires byte-exact equality with the pinned
  token composed from tracked constants (shim ABI `0.1.0`, source
  revision `1`, core `2.0.7` commit
  `2d2e5d75f28e91d61be9f293de895e2271e62a29`). Symbol presence alone is
  not identity. Missing export or token mismatch: the failed candidate
  is unloaded and the failure is thrown as `MinigrafNativeLoadException`
  naming the probed candidates and the pinned expectation. The token is
  a declared compatibility/provenance identity, not a binary-content
  hash; M3.1a measured this distinction deliberately (token unchanged
  across the rebuilt binary after the scanner fix).

## Ownership and disposal

- `MinigrafPreparedDatabase` owns one `MgsDb*` through a
  `SafeHandle` closed by `mgs_close` exactly once.
- `MinigrafPreparedQuery` owns exactly one `MgsPreparedQuery*` (plain
  nint under managed control; no finalizer, no `SafeHandle`, per ADR).
  Its native free runs under the parent gate exactly once; queries
  register with the parent at successful prepare and unregister at
  explicit disposal.
- Disposal is deterministic and idempotent at both levels. Parent
  `Dispose` under the gate: mark disposed, cascade-close every
  registered open child (children become use-rejected
  `ObjectDisposedException` and hold no native pointer afterward), then
  release the db `SafeHandle`. Child `Dispose` when the parent is
  already disposed is a harmless no-op (the cascade already freed the
  native state); explicit child `Dispose` frees its own pointer only.
- Failed construction never leaks native state: a prepare that succeeds
  natively but fails slot-name capture frees the query before throwing.
- Use after disposal (query or database, either order) throws
  `ObjectDisposedException` deterministically.

## Native gate and concurrency

- One per-database gate (same pattern as `MinigrafDatabase`) serializes
  `Prepare`, child registration/unregistration/cascade, prepared
  `Execute`, child error capture, `ExecuteRaw`, `Execute`, and
  `Dispose`. Borrowed-error sequences are atomic under the gate.
- No global managed serialization: independent prepared databases run
  in parallel; there is no shared lock across instances.
- Claims are limited to what the shim guarantees (single-writer
  semantics per engine handle at the native layer; the shim's own db
  mutex covers native-side execution). Cross-engine file behavior is
  governed by ADR 0002 hard invariants 1 and 2, qualified at M3.1a.

## Results and binding parity

- Prepared execution results decode through the existing
  `MinigrafResultDecoder` (no second decoder, no new value kinds; Ref
  and Keyword arrive flattened exactly as on the official path).
- Qualification compares result shape and values, not merely
  success/failure; representative success, empty-result, query-error,
  and binding-error cases are included; raw-vs-prepared parity is
  asserted byte-identically on identical fixtures (official
  `MinigrafDatabase` vs `MinigrafPreparedDatabase`).
- `Timestamp(i64)` keeps its measured `:as-of`/`:valid-at` substitution
  semantics; raw `:valid-at` ISO strings are never reinterpreted as
  equivalent to timestamp slots; `:as-of` counter behavior is checked
  by equality against raw `:as-of` forms (no invented `TxId` semantics).

## Qualification matrix (fixed; win-x64, net8.0 and net10.0)

Gate: identity token through the managed loader (expected token
printed), staged asset path printed.

1. open memory + Dispose + double Dispose
2. prepare → execute with val bindings (values compared)
3. prepare → multiple executes with different bindings (fresh state)
4. all supported binding kinds (entity, val String/Integer/Float/
   Boolean/Null, ValRef, ValKeyword, Transaction, Timestamp,
   AnyValidTime)
5. missing binding → `MinigrafExecutionException` (observed text
   recorded, presence-checked only)
6. extra/unknown binding ignored (upstream semantics preserved)
7. wrong binding type → `MinigrafExecutionException` (INT-035 observed
   text recorded)
8. `:as-of` via `Transaction` binding, equality against raw `:as-of`
9. `:valid-at` ISO literal prepared query vs `Timestamp(i64)` slot,
   parity
10. far-future `Timestamp(253402300799000)` parity
11. query disposal: use-after-dispose rejects; parent stays usable
    (re-prepare allowed)
12. database cascade disposal: open children rejected after cascade
13. double disposal (query then db, db then query, both harmless)
14. use-after-dispose (post-query-disposal and post-db-disposal)
15. prepare failure cleanup (transact rejection; db reusable; no leak)
16. execution failure cleanup (failed execute leaves query and db
    usable)
17. concurrent calls against one database (managed gate respected)
18. independent calls against two databases in parallel
19. shim identity mismatch/refusal: shim override pointed at the
    official binary is refused (`MinigrafNativeLoadException`), then the
    same process continues normally after resetting the override
20. raw-vs-prepared parity: same fixture through official
    `MinigrafDatabase` and prepared database; `ExecuteRaw` byte-equal;
    decoded values equal
21. managed binding prechecks: null dictionary, null/empty name, null
    keyword, non-finite float → `ArgumentNullException`/
    `ArgumentException` before any native call
22. file-backed prepared database: transact via shim, dispose, reopen,
    WAL replay visible to a prepared query
23. open-after-dispose rejection

Harness conventions: plain `Program.cs` exit-code harness, temp-fixture
cleanup with complete evidence, cleanup line printed before exit,
`MINIGRAFSHARP_SHIM_NATIVE_PATH` matrix runs before any successful
non-override load is not required (failed loads never cache), but the
refusal case resets state so later cases are unaffected.

## Framework/platform boundary

- Qualified targets: `.NET 8` and `.NET 10`, RID `win-x64` only. No
  Linux/macOS qualification is inferred from managed tests; shim assets
  for other RIDs remain unbuilt and pending (never recorded passed).
- `dotnet format --verify-no-changes` clean; solution builds clean on
  both TFMs; treatment of warnings: errors (repo-wide).

## Explicit non-goals this slice

- No packaging/NuGet assembly change: the shim ships in the package in
  a later dispatch; recorded as pending, never passed. The loader's
  staging support keeps repo-level qualification self-contained.
- No managed fallback routes, no new exception types, no
  `MinigrafDatabase` changes beyond the resolver routing, no AOT/pack
  metadata change, no RID expansion, no Gagamba/sibling changes, no
  upstream source copying.
- No async surface, no ambient transactions, no thread affinity.

## Evidence

Durable record appended to `docs/verification.md` bound to the M3.1b
source revision, host, RID, and both target frameworks, with the
harness-observed facts (including any recorded observed diagnostic
text), cleanup state, and explicit exclusions. The M3.1a anchor commit
`480471e` stays untouched.
