# Error reference

Scope: the MinigrafSharp owned native surface (the `minigrafsharp_shim`
library) and how structured error information reaches managed callers.
Managed behavior keys off structured fields and native return values only;
diagnostic text is never parsed for behavior.

## Structured error shape

Every structured failure uses one closed JSON shape:

```json
{"category": "Internal", "code": "INT-020", "message": "[INT-020] evaluator: ..."}
```

- `category` is one of the categories below.
- `code` is a stable text code, or absent/null when no trustworthy code is
  available.
- `message` preserves the native diagnostic text (for core errors this is
  the core `Display` form, `[CODE] message`).
- Owned JSON is released exactly once with `mgs_string_free`.
- Per handle: an error is stored on failure and cleared by the next
  successful operation; a later failure replaces an earlier one.

Managed exposure: `MinigrafException.NativeCategory`
(`MinigrafNativeErrorCategory`) and `MinigrafException.NativeCode`
(additive; the exception hierarchy and messages are unchanged).

## Categories

| Category | Origin | Prefix | Notes |
| --- | --- | --- | --- |
| `Parser` | Minigraf core | `PRS-` | Datalog/EDN syntax |
| `Query` | Minigraf core | `QRY-` | planning/execution |
| `Storage` | Minigraf core | `STG-` | storage/file format |
| `Wal` | Minigraf core | `WAL-` | write-ahead log |
| `Api` | Minigraf core | `API-` | engine API misuse/sequencing |
| `Internal` | Minigraf core | `INT-` | internal invariants (includes measured limit errors `INT-020` and the coarsely classified commit I/O failure `INT-000`) |
| `Shim` | MinigrafSharp owned shim | `MGS-` | owned-ABI validation/misuse |
| `Unknown` | managed fallback | — | metadata absent/malformed/untrusted; code is null |

Core categories and codes are defined by the pinned `minigraf 2.0.7`
crate and its `docs/ERROR_REFERENCE.md`; MinigrafSharp does not duplicate
that registry and does not invent or renumber core codes. Core codes are
text codes (not numeric ABI constants).

## Shim-owned error codes (`MGS-*`)

These codes are MinigrafSharp-owned, documented, stable from this
document onward, and never renumbered. They are returned in structured
JSON; managed prechecks may intercept some conditions first (see notes).

| Code | Meaning | Layer | Trigger | Stability | Visibility |
| --- | --- | --- | --- | --- | --- |
| `MGS-001` | Write transactions are thread-affine | shim interop (`mgs_tx_execute`, `mgs_tx_commit`, `mgs_tx_rollback`, `mgs_write_tx_free`) | Transaction operation from a thread other than the one that began it (including free of a still-open transaction) | stable | public via `NativeCode`; managed prechecks raise `InvalidOperationException` first, so this is primarily ABI-level |
| `MGS-002` | Terminal transaction misuse | shim interop (`mgs_tx_execute`, `mgs_tx_commit`, `mgs_tx_rollback`) | Operation after commit/rollback consumed the inner transaction | stable | public via `NativeCode`; managed raises `ObjectDisposedException` first |
| `MGS-003` | Invalid argument or schema | shim interop (all entry points) | NULL handle/string, invalid UTF-8, malformed bindings JSON, closed-schema violations in open options (unknown key, wrong type, bad `synchronous` value, out-of-range) | stable | public via `NativeCode` |
| `MGS-004` | Internal shim failure | shim interop | Poisoned database lock, non-representable text (NUL byte in generated result/slot JSON), unprepared slot scan internals | stable | public via `NativeCode` |

Not error codes: the shim identity-token mismatch and loader refusal are
managed `MinigrafNativeLoadException` failures with no native error object;
they fall back to `Unknown` classification and keep their diagnostic
message. Classic `mgs_open`/`mgs_open_in_memory` failures have no handle
and therefore no structured channel (unknown by design); use the
options-based opens for structured open diagnostics.

## Unknown fallback rule

Absent structured metadata, malformed JSON, non-object roots, missing or
wrong-typed category, unknown future categories, missing message, or
malformed encoding never hide the original failure:

`NativeCategory = Unknown`, `NativeCode = null`, message preserved (or
the deterministic fallback text). A known category without a code yields
`NativeCode = null`. Codes are never derived from message text, and a
code presented together with an unknown category is not exposed publicly.

## Managed exception mapping (unchanged)

| Failure | Exception |
| --- | --- |
| Load/identity failure | `MinigrafNativeLoadException` |
| Open failure | `MinigrafOpenException` |
| Execute/prepare/prepared/transaction failure | `MinigrafExecutionException` |
| Malformed native result | `MinigrafResultException` |
| Argument/state misuse before native use | `ArgumentNullException` / `ArgumentException` / `ObjectDisposedException` / `InvalidOperationException` |
