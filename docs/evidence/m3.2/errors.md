# M3.2d - Structured error completion: path inventory and closure matrix

Status: **implemented and qualified locally; cross-RID CI binding
appended below**. The ABI-review gate (mandate section 29) was resolved by
explicit approval of exactly one new export; implementation SHA and CI run
are recorded in the completion sections.

Baselines: M3.2b anchor `8b87d2e` (CI run 39050048369), M3.2c anchor
`90120c5` (CI run 39051498502). Qualified RIDs: win-x64, linux-x64,
osx-arm64; net8.0/net10.0. `linux-arm64`/`osx-x64` remain unqualified.

## 1. Error sources and current mechanisms

- Core-native: `minigraf::MinigrafError` exposes `category()` (Parser,
  Query, Storage, Wal, Api, Internal), `code()` (documented stable text
  codes such as `INT-020`) and `Display` (`[CODE] message`).
- Shim-owned: `store_shim_error` with `category = "Shim"` and documented
  `MGS-*` codes.
- Structured wire shape (closed): `{"category","code","message"}`; the
  message preserves the native diagnostic text.
- Handles: `ErrorState` per `MgsDb`, `MgsPreparedQuery`, `MgsWriteTx`;
  set on failure, cleared on success; later failures replace earlier ones.
  Owned JSON is freed by the caller with `mgs_string_free`.
- Managed: `NativeErrorJson.TryRead` parses and always frees; exceptions
  carry additive `NativeCategory`/`NativeCode` and preserve the message.

## 2. Closure matrix (14 paths)

| # | Path | Failure source | Diagnostic mechanism now | Structured available | Stale state | Managed exception | Gap |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `mgs_open` | core | NULL (no handle) | none | n/a | `MinigrafOpenException` | no structured channel exists in the frozen M3.1 ABI; falls back to `Unknown` (documented) |
| 2 | `mgs_open_in_memory` | core | NULL (no handle) | none | n/a | `MinigrafOpenException` | same as #1 |
| 3 | `mgs_open_with_options` | core + shim validation | NULL + owned `error_json_out` | full | per-call | `MinigrafOpenException` (structured) | none (M3.2c) |
| 4 | `mgs_open_in_memory_with_options` | core + shim validation | NULL + owned `error_json_out` | full | per-call | `MinigrafOpenException` (structured) | none (M3.2c) |
| 5 | `mgs_execute` | core + shim (UTF-8, poison, NUL) | borrowed text on db + structured JSON on db | full | cleared on success | `MinigrafExecutionException` | **managed gap**: `MinigrafPreparedDatabase.Execute*` captures text only; wire to `mgs_last_error_json` (no ABI change) |
| 6 | `mgs_prepare` | core + shim | borrowed text on db + structured JSON on db | full | cleared on success | `MinigrafExecutionException` | **managed gap**: `Prepare` captures text only; wire to `mgs_last_error_json` (no ABI change) |
| 7 | `mgs_prepared_slot_names` | shim slot scan (`MGS-004`) | borrowed text on prepared handle | stored on the prepared handle | set/replace only | `MinigrafExecutionException` | **ABI GAP**: no structured accessor for prepared handles |
| 8 | `mgs_prepared_execute` | core + shim bindings schema (`MGS-003`/`MGS-004`) | borrowed text on prepared handle | stored on the prepared handle | cleared on success | `MinigrafExecutionException` | **ABI GAP**: same as #7 |
| 9 | `mgs_begin_write` | core (`INT-001`, poison) | structured JSON on db | full | cleared on success | `MinigrafExecutionException` | none (M3.2b) |
| 10 | `mgs_tx_execute` | core + shim (`MGS-001/002/003/004`) | structured JSON on wrapper | full | replace/clear | `MinigrafExecutionException` | none (M3.2b) |
| 11 | `mgs_tx_commit` | core | structured JSON on wrapper, retained after terminalization | full | set on failure; cleared on success | `MinigrafExecutionException` | none (M3.2b; `INT-000` preserved) |
| 12 | `mgs_tx_rollback` | shim affinity/terminal | structured JSON on wrapper | full | replace/clear | managed prechecks; native path available | none |
| 13 | `mgs_write_tx_free` | shim affinity; NULL | structured JSON on wrapper (alive after refusal) | partial by design | n/a | managed frees only after terminalization | none (NULL has no channel by contract) |
| 14 | shim identity verification / loader refusal | managed loader | `MinigrafNativeLoadException` text | none (no native error object) | n/a | `MinigrafNativeLoadException` | by design: `Unknown` fallback, diagnostic preserved |

Managed-side prechecks (disposed/thread-affinity/argument validation) are
not native failures: they throw the existing deterministic exceptions
before any native call and carry no structured metadata by design
(unchanged).

## 3. The hard gap (mandate section 29)

`mgs_prepared_slot_names` and `mgs_prepared_execute` store structured
`ErrorState` on the `MgsPreparedQuery` handle, but the ABI exposes only:

- `mgs_last_error_json(const MgsDb*)` (database handles)
- `mgs_tx_last_error_json(const MgsWriteTx*)` (transaction handles)

There is no `..._json` accessor for prepared handles, so managed prepared
errors cannot expose category/code without either a new export or an
unsafe workaround.

Alternatives considered and rejected:

1. **Dual-write prepared errors to the parent database handle.** The
   prepared query holds an `Arc<EngineCell>`, not an `MgsDb`; storing a
   raw db pointer would dangle after `mgs_close` (M3.1 allows closing the
   database while a prepared query remains alive because the query holds
   engine clones), creating use-after-free on a later prepared error.
2. **Polymorphic `void*` accessor.** Type-unsafe and violates the
   explicit-ownership ABI rules (a caller could pass a db pointer where a
   prepared pointer is expected, or vice versa).
3. **Move prepared errors onto the database handle.** Breaks the M3.1
   contract (`mgs_prepared_last_error` is handle-local) and the
   concurrency/ownership story.

Proposed minimal addition (subject to explicit review):

```c
// C ABI: char *mgs_prepared_last_error_json(const MgsPreparedQuery *prepared);
// Owned structured-error JSON (same closed shape) or NULL when there is no
// error. Frees with mgs_string_free. Cleared by the next successful
// prepared operation; failures replace earlier ones.
char *mgs_prepared_last_error_json(const MgsPreparedQuery *prepared);
```

This mirrors `mgs_prepared_last_error` exactly, adds one export
(22 → 23), introduces no ownership/lifetime change (same borrowed handle,
same owned-string mechanism), and is the only way to expose the already
stored structured state. ADR 0003 has been updated with the proposed
amendment; implementation is intentionally not started.

## 4. Rules this slice will encode once approved

- Unknown fallback (strict): absent, malformed, non-object, missing/
  wrong-typed category, unknown future category, missing message, or
  malformed encoding → `NativeCategory = Unknown`, `NativeCode = null`,
  message preserved (or the existing fallback text). A known category
  with a missing code yields `NativeCode = null` (never inferred).
  A structurally valid code presented with an unknown category is
  **dropped** from the public surface (wire format may echo it).
- The managed parser (`NativeErrorJson`) gains a pure, defensive
  `Parse(string?)` entry point (no mutable state, never throws) so
  malformed metadata can be exercised by tests without a production seam;
  `TryRead` keeps the single free-once path.
- No message parsing anywhere: classification comes only from the JSON
  fields; message text is diagnostics.
- Exception hierarchy and messages unchanged; only additive fields are
  populated.
- Native tests (Rust, under `cargo test --locked`) will cover core/shim
  conversion JSON, stale clearing, replacement, failed-commit
  post-terminal retrieval and malformed-serialization defense.
- A dedicated managed harness (`MinigrafSharp.StructuredErrorQualificationTests`)
  will cover the 18 mandated cases; `MGS-001`/`MGS-003` are additionally
  covered at ABI level in the shim harness because managed prechecks
  intercept those conditions before native use.
- Docs: `docs/error-reference.md` (MGS registry + core mapping).
- Evidence: this file is completed with implementation/qualification
  results after approval; cross-RID CI run appended.

## 5. Approved resolution

The `mgs_prepared_last_error_json` accessor and the strict unknown rule
were approved (2026-10-17). One export was added; the true inventory is
**22 exports** (12 M3.1 + 7 M3.2b + 2 M3.2c + 1 M3.2d). The aggregate
count drift is recorded in the M3.2b/M3.2c evidence corrections and in
ADR 0003; no ABI content was missing, and the generated header is
authoritative. No public type was added (surface stays at 23).

## 6. Implementation

- `mgs_prepared_last_error_json` mirrors `mgs_prepared_last_error`
  (owned JSON, `mgs_string_free`, NULL when clean, NULL handle is NULL,
  cleared on success, failures replace). Header regenerated by cbindgen
  0.29.4 and explicitly repinned:
  `shim.header.sha256
  38B7FEE0FD4A1454D63E12E0CEEBCD22B756316E342E719D88E95C20570FBA92`,
  6745 bytes. Local win-x64 staged shim:
  `78980A679BDF7B88802705C9AB1E56172E97C0C2124D5609B392658CB056BDCF`,
  2279424 bytes (CI re-records per-RID hashes).
- Core conversion is centralized: `core_error_parts()` is the single
  extraction of category/code/message used by handle storage and the
  open-failure out-parameter; shim failures use `store_shim_error`.
  Fields come from the structured accessors, never from Display parsing.
- Managed parser (`NativeErrorJson`) is centralized, pure and defensive
  (`Parse`, no mutable state, never throws; `TryRead` keeps a single
  free path). Strict rules implemented: absent/malformed/wrong-typed/
  unknown-category metadata → `Unknown`/null; known category with a
  missing or structurally invalid code → category/null; a code presented
  with an unknown category is dropped; codes are never inferred from
  message text.
- Managed wiring: `MinigrafPreparedDatabase.Execute*`/`Prepare` and
  `MinigrafPreparedQuery` (slot names and prepared execute) now read the
  structured JSON first and only fall back to borrowed text when no
  structured state exists (Unknown classification, diagnostic preserved).
  `BeginWrite` and transaction paths were already structured. Exception
  hierarchy and messages are unchanged.
- `docs/error-reference.md` records the MGS-001..004 registry (layer,
  trigger, stability, visibility), the core category/code mapping and
  the unknown rule.

## 7. Qualification executed (local win-x64, both TFMs)

- Rust: 13 unit tests (6 new: core conversion, shim conversion, stale
  clear/replacement, malformed-serialization defense, failed-commit
  post-terminal retrieval, prepared JSON ownership/NULL) + 18
  characterization probes, all green under `cargo test --locked`.
- New `tests/MinigrafSharp.StructuredErrorQualificationTests`: 19/19 on
  net8.0 and net10.0. Measured qualified cases: `PRS-*` (Parser),
  `API-003` (Api), `QRY-007` (Query), `INT-020` (Internal, options
  limit), `INT-001` (Internal, same-thread implicit execute),
  `INT-000` (Internal, induced commit failure; diagnostics retained after
  terminalization), `INT-033` and `INT-035` (prepared bind failures),
  prepared `QRY-007`, structured options-open failure
  (`Internal/INT-000` with OS diagnostic), failure replacement, stale
  clearing observed through subsequent classification, absent-metadata
  classic open (`Unknown`/null, message preserved), malformed payloads
  (`not-json`, `{}`, wrong-typed category/message, missing message),
  unknown future category with a valid-looking code (dropped),
  missing/invalid code (category preserved, code null), and a
  code-looking message proving no message-derived classification.
  `MGS-001`/`MGS-003` are covered at ABI level in the shim harness (24/24)
  and through the managed parser mapping; managed prechecks intercept the
  corresponding misuse conditions before native use by design.
- Regression: shim harness 24/24, transaction harness 18/18, open-options
  harness 11/11, prepared harness 26/26, unit (23-type public-surface
  closure), integration and qualification suites — all green on both
  TFMs; solution builds 0 warnings; format clean.
- Cleanup: error harness prints `cleanup: complete=true`; the forced
  commit-failure fixture removes its WAL blocker; no fixture survives a
  passing run.

## 8. Independent review (performed 2026-10-17)

Checked and passed:

- No message parsing: the managed parser never inspects message text;
  grep over `src/MinigrafSharp` finds no code/message pattern matching,
  regex, or substring classification. A known category plus a
  code-looking message yields null `NativeCode` (tested).
- No category guessing: unknown/missing/wrong-typed categories map to
  `Unknown`; the exhaustive enum match has a fallback, and tests cover a
  future category.
- Unknown category drops a seemingly valid code (tested).
- Parser failures cannot replace the operation failure: `Parse` catches
  all exceptions; callers always throw the operation exception.
- No stale error survives success: Rust tests cover clear-on-success and
  replacement; managed tests cover post-success reclassification.
- Failed commit diagnostics remain readable after terminalization
  (Rust and managed tests).
- Shim failures are labeled `Shim/MGS-*`, core failures keep native
  category/code (Rust conversion tests).
- Buffer discipline: `TryRead` frees in `finally`; open failures consume
  the out-parameter JSON; transaction failures read before freeing the
  wrapper. No double free or leak path was found in review.
- No ABI growth beyond the approved single export (22 total), no new
  public type (23 types), public exception hierarchy unchanged.
- NativeAOT safety: parser uses `JsonDocument` only (no reflection
  serialization); verification via the existing AOT consumers in CI.
## 9. Cross-RID CI qualification (M3.2d)

- Qualified source (implementation): `6e17b88`.
- CI run https://github.com/jenolaszlo-sketch/minigraf-sharp/actions/runs/39053109905
  on `6e17b88` (all 11 jobs green):
  - Rust 13 unit tests + 18 probes on each `dist` runner;
  - structured-error harness 19/19 on net8.0 and net10.0 on win-x64,
    linux-x64 and osx-arm64;
  - shim 24/24, transactions 18/18, options 11/11, prepared 26/26 on
    both TFMs per RID; packaging, verifier/tamper and NativeAOT
    consumers green (M3.1/M3.2b/M3.2c regression intact).
- Evidence-bound shim artifacts recorded in `native/upstream.json`
  (`qualified: true`, rustc 1.99.1 / cbindgen 0.29.4 per RID):

| RID | Shim SHA-256 | Bytes |
| --- | --- | --- |
| win-x64 | `2F8CDF1C89FD4CBEBCF48FAFB9A4C773136DBBCDE341D6857590558AB36FCAF0` | 2279424 |
| linux-x64 | `E39EFCAC8CDB6C25F41D1E46DE3FF958556D292A64FFEA23FDE99C670B54A816` | 2959248 |
| osx-arm64 | `011315B88CA30F6F807F933F28E8E4ADE099872FC5D1F2FA9D9F79234FD28017` | 2535024 |

Downloaded staging artifacts were re-verified locally
(`eng/Verify-NativeStaging.ps1`): all three RIDs pass with no divergence
notes. ABI inventory: 22 exports; public surface: 23 types.
`linux-arm64`/`osx-x64` remain unqualified. No publication occurred.
