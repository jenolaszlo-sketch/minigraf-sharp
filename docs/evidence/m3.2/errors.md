# M3.2d - Structured error completion: path inventory and closure matrix

Status: **blocked at the ABI-review gate (mandate section 29)**. The
inventory below is complete; one hard gap requires a new export before
implementation. No implementation or qualification claim is made in this
document yet.

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

## 5. Blocked status

Implementation has not begun. The only open decision is the proposed
`mgs_prepared_last_error_json` export (23rd) and the strict
unknown-category/code rule above. Everything else in M3.2d requires no
ABI change and no new public type (public surface stays at 23 types).
