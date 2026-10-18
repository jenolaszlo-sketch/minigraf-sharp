# M3.2g - ADR 0003 closure and independent release review

Status: review performed on the M3.2g integrated-qualification revision.
Decision: **the bounded M3.2 capability is release-ready for a controlled
preview release**, which remains a separate dispatch. No publication
occurs in this slice.

Reviewed inputs: ADR 0003 (accepted 2026-10-17, with amendments),
`docs/evidence/m3.2/` per-slice evidence (characterization, transactions,
open-options, errors, values, identity) and
`docs/evidence/m3.2/qualification.md` (M3.2g integrated scenarios and
cross-feature invariants), and the M3.1 record in `docs/verification.md`.

## 1. ADR 0003 requirement-by-requirement closure

Outcomes: **PASS** (implemented and independently qualified), **SUPPORTED
WITH QUALIFICATION** (implemented with a bounded, explicitly recorded
claim), **OUT OF SCOPE** (deliberately excluded), **BLOCKED** (cannot be
qualified on the available hosts/inputs).

### 1.1 Scope

| # | Requirement | Outcome | Evidence |
| --- | --- | --- | --- |
| S1 | Explicit write transactions | PASS | M3.2b `transactions.md` (18/18); integrated B/C/I1-I4/I6/I7 |
| S2 | Typed database open options (core subset) | PASS | M3.2c `open-options.md` (11/11); integrated A |
| S3 | Structured native error information | PASS | M3.2d `errors.md` (19/19); integrated C/I7 |
| S4 | Typed Ref and Keyword result values | PASS | M3.2e `values.md` (15/15); integrated D/I8 |
| S5 | Runtime native/core identity exposure | PASS | M3.2f `identity.md` (14/14); integrated E/I9 |
| S6 | Qualification and packaging of the above | PASS | M3.2g `qualification.md`; CI `dist`/`pack`/`consume` jobs |

### 1.2 ABI additions (closed set)

| Requirement | Outcome | Evidence |
| --- | --- | --- |
| Exactly the approved exports, 12 (M3.1) + 7 (M3.2b) + 2 (M3.2c) + 1 (M3.2d) = **22** | PASS | generated header is authoritative, pin `38B7FEE0...BA92` (6745 bytes); shim harness 26/26; ABI inventory correction recorded in `errors.md §5` and ADR 0003 |
| Explicit ownership (owned JSON freed by caller; borrowed identity unchanged) | PASS | `errors.md`; Rust ownership tests; review §3 (error lifetime) |
| Explicit lifetime (transaction freed exactly once by commit/rollback/free) | PASS | `transactions.md`; integrated I2/I7 |
| Deterministic error contract (NULL/-1 plus last-error JSON, never a silent default) | PASS | `errors.md` closure matrix (14 paths) |
| Documented nullability and threading (one transaction per database, engine-serialized; no cross-thread use) | PASS | ADR gate/threading contract; integrated I1; M3.2b thread-affinity cases |
| Amendments 1-3 (wrapper lifetime, dual-boundary thread affinity, `error_json_out`) | PASS | ADR approval record; `transactions.md`, `open-options.md`; integrated I7 |

### 1.3 Behaviors and models

| Requirement | Outcome | Evidence |
| --- | --- | --- |
| Stale-error clearing: set on failure, cleared on success, failures replace | PASS | `errors.md`; Rust clear/replace tests; managed reclassification case |
| Failed-commit diagnostics readable after inner transaction terminalized | PASS | `transactions.md`; integrated I7 |
| Shim failures labeled `Shim`/`MGS-001..004`; core failures keep native category/code | PASS | `errors.md`; `docs/error-reference.md` |
| Strict unknown rule (absent/malformed/unknown-category -> `Unknown`/null; no text-derived codes) | PASS | `errors.md`; `NativeErrorJson` parser cases |
| Managed transaction state machine (Open -> Committed/RolledBack/Disposed; Dispose = rollback) | PASS | `transactions.md`; integrated I2 |
| Database owns live transactions; disposal cascades (rollback), owner-thread only, no finalizers | PASS | `transactions.md` cascade/thread cases; integrated I1 |
| Gate/threading contract: no gate held across transaction lifetime; native-first terminalization (deadlock-freedom) | PASS | ADR §"Gate and threading contract"; review §3 (deadlock) |
| Typed Ref/Keyword closed envelope; managed kinds `Ref`/`Keyword`; official `MinigrafDatabase` keeps flattened strings | PASS | `values.md`; integrated D/I8 |
| Open-options closed mapping; invalid forms fail before native use; defaults preserved | PASS | `open-options.md`; integrated A |
| Identity policy: stable concepts only; hashes remain evidence; verification is the loader gate | PASS | `identity.md`; integrated E/I9 |
| M3.1 invariants 1-7 remain in force for the extended surface | PASS | M3.1 record + M3.2 reruns; integrated composition |
| Prepared-query semantics frozen (committed facts only inside a transaction) | SUPPORTED WITH QUALIFICATION | measured native contract; integrated I3; limitation preserved below |
| Rules inside a transaction are not transactional | SUPPORTED WITH QUALIFICATION | measured native contract; integrated I5; limitation preserved below |
| `Synchronous` accept/reject only, no durability claim | SUPPORTED WITH QUALIFICATION | `open-options.md`; limitation preserved below |

### 1.4 Approved gates

| Gate | Outcome | Evidence |
| --- | --- | --- |
| 1. Public managed types and placement on `MinigrafPreparedDatabase` | PASS | closure test 24 types; integrated cases |
| 2. ABI additions as amended | PASS | 22 exports; header pin |
| 3. Disposal = rollback; revised thread/gate contract | PASS | `transactions.md`; integrated I1/I2 |
| 4. Typed Ref/Keyword decode change with M3.1 requalification | PASS | `values.md`; M3.1 prepared/shim harnesses rerun green |
| 5. Closed options subset and `Unlimited` sentinel | PASS | `open-options.md` |
| 6. Error surface (`NativeCategory`/`NativeCode`, `Shim`/`Unknown`, unknown path, clearing) | PASS | `errors.md` |

### 1.5 Exclusions

| Requirement | Outcome |
| --- | --- |
| Nested/ambient/distributed transactions; automatic retry; implicit scopes | OUT OF SCOPE |
| Schema/ORM/LINQ/change-tracking/pooling; new query features; checkpoint on the shim; v3 APIs | OUT OF SCOPE |
| Performance work beyond the new surface | OUT OF SCOPE |
| Publication | OUT OF SCOPE (separate dispatch) |
| `page_cache_size` / `allow_unlocked` options | OUT OF SCOPE (no qualified meaning / not exercisable on qualified hosts) |
| `linux-arm64` / `osx-x64` | BLOCKED (no hosts/inputs; never recorded as passed) |

### 1.6 Preserved limitations (explicit)

- **Rules are not transactional**: a rule registered while a write
  transaction is open takes effect immediately and survives rollback
  (integrated I5). This is measured native behavior, documented as a
  contract, not a defect.
- **Prepared-query visibility**: a prepared query executed inside an open
  transaction sees committed facts only, not staged facts (integrated I3);
  transactional Datalog reads see both (I4).
- **`Synchronous.Normal` durability**: accepted and exercised for
  accept/reject and reopen; no durability claim is made under OS crash or
  power loss (no crash-injection qualification this milestone).
- **Unsupported RIDs**: `linux-arm64` and `osx-x64` remain unqualified.
- **Identity is declared provenance**: `MinigrafRuntimeIdentity` reports
  build-declared compatibility/provenance, not a binary measurement; exact
  per-RID hashes remain qualification evidence.

## 2. Frozen surface

- Native ABI: **22 exports**, frozen. Generated header is authoritative;
  pin `38B7FEE0FD4A1454D63E12E0CEEBCD22B756316E342E719D88E95C20570FBA92`
  (6745 bytes), unchanged through M3.2e/M3.2f/M3.2g.
- Public managed surface: **24 types**, frozen for the preview line. The
  M3.2 additions are `MinigrafOpenOptions`, `MinigrafSyncMode`,
  `MinigrafNativeErrorCategory`, `MinigrafWriteTransaction`,
  `MinigrafRuntimeIdentity` plus the append-only `MinigrafValueKind.Ref`/
  `Keyword` values; the M3.1 surface is unchanged. Enforced by the closure
  test in `tests/MinigrafSharp.Tests` (both TFMs).

Any change to either set requires a new ADR revision and requalification.

## 3. Independent release review (whole M3.2 capability)

| Focus area | Finding |
| --- | --- |
| Cross-feature ownership | No leak or double-free path found. The database cascades open transactions and prepared queries (rollback, never commit); terminalized wrappers free exactly once; failed opens leave no handle; identity refusals cache nothing. Integrated I1/I2/I7 and the M3.2b/d ownership tests agree. |
| Deadlock potential | The managed per-database gate is never held across a transaction lifetime; transaction operations hold only the transaction lock; terminalization is native-first (releases the engine write mutex before taking the gate). A concurrent `db.Execute` blocked on the engine mutex cannot block the owner's commit. Integrated I1/I4 and the M3.2b concurrency case (blocking then completion) show no deadlock. |
| Error lifetime | Structured diagnostics are set on failure, cleared on success, replaced by later failures, and retained on the wrapper after commit/rollback consumes the inner transaction (integrated I7); owned JSON is freed exactly once on every path (`errors.md`). No stale leak or premature free found. |
| Typed-value compatibility | The Ref/Keyword divergence is intentional and bounded to the owned surface; scalar kinds keep byte parity with the official C layer, and official `MinigrafDatabase` keeps flattened strings (integrated D/I8, `values.md`). No scalar regression; the only compatibility change is documented in the CHANGELOG and ADR. |
| Native identity | Identity verification is the loader gate for every open path (integrated E/I9); wrong binaries are refused before use and never poison later loads; the exposed identity is declared provenance, not a hash. Mismatch negatives covered in `identity.md`. |
| Package provenance | Per-RID shim + engine hashes, toolchains and manifests are recorded in `native/upstream.json` and re-verified by `eng/Verify-NativeStaging.ps1`; packaging verifies the staged chain of custody and the consumer/tamper checks run in CI. No hand-edited artifact hashes. |
| NativeAOT behavior | All managed decoding uses `JsonDocument`/manual parsing (no reflection serialization); the structured-error parser and result decoder are AOT-safe; the existing NativeAOT consumers in CI pass for this surface. |

Findings: none requiring a change. No historical qualification evidence
was modified; this review adds the M3.2g composition evidence only.

## 4. Release readiness verdict

- All five integrated scenarios pass and all nine cross-feature invariants
  hold on the qualified platforms (win-x64 locally and in CI;
  linux-x64/osx-arm64 in CI), on net8.0 and net10.0, with the full
  regression matrix green.
- ADR 0003 is closed: every requirement has a recorded outcome; the only
  non-PASS items are deliberate bounded claims and out-of-scope/blocked
  items, each explicitly preserved above.
- The 22-export native ABI and 24-type public managed surface are frozen.
- **Verdict: ready for a controlled preview release**, to be performed as
  a separate, artifact-preserving dispatch (version bump at release-prep,
  trusted publishing of the exact CI-produced package, chained
  verification), following the process proven for `0.1.0-preview.4`.
- **Exact qualified artifact**: the package produced by CI for the M3.2g
  qualified source revision (recorded in the CI-binding section of
  `qualification.md`); the release candidate is that CI artifact,
  downloaded and verified, never rebuilt.

## 5. Exclusions

- Publication is out of scope for M3.2g.
- `linux-arm64` and `osx-x64` remain unqualified.
- No API/ABI/semantic change is part of this review; it records tested
  behavior and closure only.

## 6. Release record (M3.2 released)

M3.2 is both qualified and released. The versioned release revision is
`700bcf5` (metadata-only bump to `0.1.0-preview.5`), qualified by CI run
39095902696, published by `publish.yml` run 39096585262 (trusted
publishing, no rebuild), and verified on the public feed by
`verify-published.yml` run 39096615454. Package
`MinigrafSharp.0.1.0-preview.5.nupkg`, SHA-256
`28732C044910A8CCEC5CB96A03FBBA38246255C5AE925AF4524E20E8F599FE11`;
annotated tag `v0.1.0-preview.5`. The M3.2g qualified source `33bda67`
(evidence `1bea537`) remains the capability qualification anchor. Full
details in `docs/verification.md` (release record).
