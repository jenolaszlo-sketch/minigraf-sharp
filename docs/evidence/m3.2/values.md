# M3.2e - Typed Ref and Keyword values (evidence)

Status: implemented and locally qualified on win-x64 (net8.0/net10.0);
cross-RID CI qualification appended below/after the slice commit. Basis:
ADR 0003 (accepted) typed-value section; anchors M3.2d source `6e17b88`,
evidence `48d7ecc`.

## Bounded compatibility change (approved under ADR 0003)

M3.1 established byte parity between official minigraf-c and shim result
JSON. M3.2e intentionally narrows that parity for exactly two value kinds:

- All other value kinds (String, Integer, Float, Boolean, Null) keep
  byte-identical serialization (proven by direct raw-JSON comparison).
- Ref and Keyword intentionally diverge from the official C
  serialization because that layer flattens them into JSON strings and
  loses the native type. The shim now serializes:
  - `{"type":"ref","value":"<canonical UUID>"}`
  - `{"type":"keyword","value":":<exact text>"}`
- The official `MinigrafDatabase` path is untouched and keeps flattened
  `String` values (proven).

This is the approved compatibility change, not a regression; the M3.1
"raw-vs-prepared byte parity" claim is superseded for ref/keyword values
only. Non-typed parity remains the M3.1 contract.

## Implementation

- Native serializer (`value_to_json`): typed envelopes for
  `Value::Ref(Uuid)` and `Value::Keyword(String)`; every other arm is
  byte-identical to the official shapes. No new exports; no header
  change (pinned `38B7FEE0...BA92`).
- Managed `MinigrafValueKind` gains `Ref` and `Keyword` (append-only;
  no reordering, no removed kinds). `MinigrafValue` gains `GetRef()`
  (`Guid`) and `GetKeyword()` (exact string, colon-prefixed,
  case-sensitive), plus kind-sensitive equality, hash and formatting.
  Accessors for the wrong kind throw `InvalidOperationException`.
- Decoder (`MinigrafResultDecoder`): objects are accepted only as the
  closed envelope (exactly `type` + `value`, both strings). `ref`
  requires a canonical hyphenated UUID (`Guid.TryParseExact` "D");
  `keyword` requires a non-empty colon-prefixed string; unknown kinds,
  extra/missing/non-string fields are `MinigrafResultException`.
  Ordinary strings that resemble typed values remain `String`; no type
  is inferred from text.
- Surface: no new public types (still 23); the enum gains two members.
  `MinigrafPreparedDatabase` ordinary and prepared execution, and
  `MinigrafWriteTransaction` execution, all decode typed values; the
  official surface keeps flattened strings.

## Round trip and type integrity

- Storage -> typed decode: `#uuid` refs decode as `Ref(Guid)` with UUID
  identity preserved; keywords decode with exact case (`:Status/Active`
  is not equal to `:status/active`).
- Existing typed bind kinds (`ValRef`, `ValKeyword`, `Entity`) drive the
  prepared path unchanged; results stay typed. No binding-schema
  expansion was needed (no measured gap).
- Equality: typed `Ref` never equals a same-looking `String`; keyword
  equality is case-sensitive; equal refs/keywords hash equal.
- Negative controls (decoder seam): malformed/braced UUIDs, missing or
  extra fields, non-string fields, unknown type names, keywords without
  the colon, empty keywords and empty envelopes all raise
  `MinigrafResultException` (never silently become valid values).
  Arrays/other shapes remain rejected.

## Qualification executed (local win-x64)

- New `tests/MinigrafSharp.TypedValueQualificationTests`: 15/15 on
  net8.0 and net10.0 (typed decode on ordinary/prepared/transaction
  paths, typed binding round trips, official flattening preserved,
  scalar byte parity, documented typed divergence, string-looks-like
  non-inference, malformed/unknown envelopes, invalid keywords,
  kind-sensitive accessors, equality/hash/formatting).
- Rust: 14 unit tests (new serializer-shape test for ref/keyword and
  scalar arms) + 18 characterization probes under `cargo test --locked`.
- Shim harness: 26/26 (2 new cases: typed envelopes with scalar
  shapes at the ABI, official C flattening positive control).
- Regression: prepared 26/26 (parity fixture uses only non-typed values;
  the entity-UUID helper reads typed Ref, keyword read updated to the
  typed kind), transactions 18/18, options 11/11, structured errors
  19/19, unit (23-type closure), integration and qualification suites
  green on both TFMs; solution builds 0 warnings; format clean.
- Cleanup: typed harness prints `cleanup: complete=true`; no fixture
  survives a passing run.

## Independent review checklist (performed)

- Type inference from text: none (UUID/keyword-looking strings stay
  String; envelope type is the only discriminator).
- Official behavior unchanged: positive control shows flattened
  strings; no official code path touched.
- Scalar serialization unchanged: per-kind raw-JSON byte equality.
- Malformed envelopes fail closed with `MinigrafResultException`; no
  silent coercion, no partial decode.
- No new exports, no ABI drift (22 exports), no new public type
  (23 types); enum additions are append-only.
- No transaction/open/error/loader semantics changed.
- AOT safety: `JsonDocument`-based decoding only; no reflection
  serialization; AOT consumers re-run in CI.

## Limitations

- Typed decoding requires the owned shim; the official minigraf-c
  surface cannot provide it without upstream changes (recorded
  limitation, not a pending gap).
- `Value::Float` non-finite values still arrive as JSON null (official
  C behavior; unchanged).
- Cross-RID CI results and per-RID shim hashes are appended after the
  slice run; `linux-arm64`/`osx-x64` remain unqualified. No publication.
