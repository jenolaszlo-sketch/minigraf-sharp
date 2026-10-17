# M3.1a checkpoint proposal — owned prepared-query shim ABI + identity (win-x64)

Status: proposed 2026-10-17. Executed under approved ADR 0002
(`docs/decisions/0002-prepared-queries-owned-shim.md`) after the
maintenance checkpoint (`4447033`) landed with its requalification
evidence. This record fixes the exact shim ABI, identity policy, bindings
schema, build provenance, and the M3.1a harness case list that ADR 0002's
test section defers to "the checkpoint record reviewed with this ADR".

Known-issue status inputs (measured at `4447033`, cited by ADR 0002's
ordering rule): #554/#555/#558 not reachable through the serialized
wrapper surface (v2.0.6 engine fixes adopted by the pin); #603 fixed
core-side at 2.0.7, managed recheck inside M3.1 via the shim; #371/#287,
#477, #435, #374, #535 still open on the 2.x line.

## Scope of M3.1a

Native-only slice: the owned `mgs_` shim library, its identity probe, its
build provenance, and a standalone native harness that proves identity and
basic prepared-query mechanics. No managed public API, no managed loader
integration, no `MinigrafDatabase` change, no packaging change beyond
adding the shim asset under `native/staging/win-x64/`. M3.1b (separate
checkpoint) wires the managed loader, public `MinigrafPreparedDatabase` /
`MinigrafPreparedQuery` management surface, cascade ownership, and the
full behavioral qualification matrix.

## Verified implementation inputs (exact sources, this slice)

- Crates.io package `minigraf` version `2.0.7`, tarball SHA-256
  `74E6724A59FECE46F0F56C3FE16F1792B34E34FE08FDFF4C902B22696D760D68`
  (1,443,572 bytes), contents inspected directly this slice. Its public
  surface exports exactly what the shim binds:
  `Minigraf::open(path)`, `Minigraf::in_memory()`, `Minigraf::execute(&str)`,
  `Minigraf::prepare(datalog)` (db.rs), `PreparedQuery::execute(&[(&str, BindValue)])`
  and `PreparedQuery` slot collection (prepared.rs), `BindValue`
  (`Entity(Uuid)`, `Val(Value)`, `TxCount(u64)`, `Timestamp(i64)`,
  `AnyValidTime`), `Value` (`String`/`Integer`/`Float`/`Boolean`/`Ref(Uuid)`/
  `Keyword(String)`/`Null`), `QueryResult`
  (`Transacted(u64)`/`Retracted(u64)`/`Ok`/`QueryResults{vars, results}`),
  `MinigrafError` with `code()` (`Api010`, `Int033`, `Int034`, `Int035`).
  `PreparedQuery` is owned and holds `FactStorage` + `Arc<RwLock<RuleRegistry>>`
  + `Arc<RwLock<FunctionRegistry>>` clones; extra bindings are silently
  ignored; attribute slots are rejected at prepare (INT-034); missing
  slots INT-033; wrong kind INT-035; literal `:as-of`/`:valid-at` values
  are kept, only slots substituted (#603). All statements are re-measured
  facts from the tarball, not carried from the proposal audit.
- Official binding serialization reference: upstream `minigraf-c` v2.0.7
  `src/lib.rs` (SHA-256
  `B14CA2F167B989309310A5052B76E30E9072A6534FD5A22A1DF52F767E8B65B9`,
  fetched into the scratch area only as the parity fixture source; no
  upstream file is copied into the shim):
  - result shapes `{"transacted":<n>}`, `{"retracted":<n>}`,
    `{"ok":true}`, `{"variables":[…],"results":[[…]]}`;
  - `Value::Ref(u)` serializes as `u.to_string()` (hyphenated lowercase
    UUID text), `Value::Keyword(k)` as the keyword text (which already
    includes the leading `:`), `Value::Float` via
    `Number::from_f64(...)` (non-finite becomes null), `Value::Null` as
    JSON null;
  - error text uses `format!("{e:#}")` on `MinigrafError`.
- Toolchain installed this slice: rustup stable
  `x86_64-pc-windows-msvc`, rustc/cargo `1.99.1 (5f94df478)`, MSVC
  Build Tools 18 + Windows SDK 10 already present on the host.

## Owned shim design

### Source layout (all files owned by this repository)

```text
native/shim/
  Cargo.toml            package minigrafsharp-shim, version 0.1.0,
                        edition 2024, lib crate-type ["cdylib","staticlib"]
  identity.json         shim ABI identity (single source of truth)
  build.rs              reads identity.json + ../upstream.json,
                        exports MGS_SHIM_IDENTITY_TOKEN
  Cargo.lock            committed, crates.io checksums
  src/lib.rs            the whole shim (no upstream file copied)
  cbindgen.toml         header generation config
native/include/minigrafsharp_shim.h   generated + archived + hashed
```

Dependencies (pinned in `Cargo.lock` with crates.io checksums):
`minigraf = "=2.0.7"`, `serde_json = "1"`, `uuid = "1"`. No other third
party crates. No fork, no sibling checkout, no upstream shell source.

### Handle model

```rust
pub struct MgsDb { db: Mutex<Minigraf>, last_error: Mutex<Option<CString>> }
pub struct MgsPreparedQuery { pq: PreparedQuery, last_error: Mutex<Option<CString>> }
```

- `MgsPreparedQuery` does not touch `Minigraf` at execute time (the
  prepared query carries its own shared-state clones), but prepared
  execute acquires the parent database mutex anyway: the shim surface is
  serialized per-database at the native layer exactly like the official
  library, so both libraries expose the single-writer discipline
  independently of the managed gate. The managed per-database gate
  (M3.1b) adds no native replacement for this.
- Error text is stored per handle as the `{e:#}` rendering (official
  convention); borrowed `last_error` pointers stay valid until the next
  call on the same handle; failed opens expose no diagnostic handle
  (same honesty as the official library).

### Exports (exact set: 11 + identity probe = 12)

Verbatim from ADR 0002 (no additions beyond it):

- `MgsDb *mgs_open(const char *path)` — NULL on failure.
- `MgsDb *mgs_open_in_memory(void)` — NULL on failure.
- `void mgs_close(MgsDb *handle)` — exactly once per owned handle; NULL safe.
- `char *mgs_execute(MgsDb *handle, const char *datalog)` — owned result
  JSON, official shapes verbatim; NULL on error with `mgs_last_error`.
- `const char *mgs_last_error(const MgsDb *handle)` — borrowed.
- `const char *mgs_prepared_last_error(const MgsPreparedQuery *prepared)` — borrowed.
- `void mgs_string_free(char *s)` — NULL safe.
- `MgsPreparedQuery *mgs_prepare(MgsDb *handle, const char *datalog)` —
  NULL on error; diagnostic stored on the same db handle.
- `char *mgs_prepared_slot_names(const MgsPreparedQuery *prepared)` —
  owned JSON array of the sorted, deduplicated slot names.
- `char *mgs_prepared_execute(MgsPreparedQuery *prepared, const char *bindings_json)`
  — owned result JSON (official shapes) or NULL (diagnostic on the
  prepared handle).
- `void mgs_prepared_query_free(MgsPreparedQuery *prepared)` — once; NULL safe.
- `const char *mgs_shim_identity(void)` — static, process-lifetime
  borrowed token.

Not exported: `checkpoint`, options opening, cursors, fact logs, writers
(ADR non-goals; unchanged).

### Bindings JSON schema (M3.1a native contract, reused by M3.1b)

Top level: a JSON array of objects. Each object: `"name"` (string) and
`"bind"` (object). `bind.kind` selects the variant; extra properties are
rejected (strict closed schema; no caller-controlled fragments).

| bind.kind | payload | core BindValue |
| --- | --- | --- |
| `entity` | `value` = canonical UUID text | `Entity(Uuid)` |
| `val` | `type`/`value`: `string`/`integer`/`float`/`boolean`/`null`; non-finite floats rejected with a parse error before substitution | `Val(Value::…)` |
| `ref` | `value` = canonical UUID text | `Val(Value::Ref(u))` (rule-invocation argument identity) |
| `keyword` | `value` = keyword text | `Val(Value::Keyword(k))` |
| `tx_count` | `value` = u64 | `TxCount(u64)` |
| `timestamp` | `value` = i64 unix ms | `Timestamp(i64)` |
| `any_valid_time` | (none) | `AnyValidTime` |

Malformed bindings (non-array, missing/wrong-typed fields, unknown kind,
null name, duplicate accounting follows last-wins as upstream HashMap
semantics) produce NULL + diagnostic text on the prepared handle; the
diagnostic states the parse failure; error-code text of core-substitution
errors flows through unchanged. No managed skill: the shim never parses
error text.

### Identity probe and provenance (hard invariant 6)

`mgs_shim_identity()` returns one static compact JSON token, composed at
build time from two tracked sources (both `cargo:rerun-if-changed`):

```json
{"shim_abi":"0.1.0","shim_source_revision":1,"core_version":"2.0.7","core_commit":"2d2e5d75f28e91d61be9f293de895e2271e62a29"}
```

- `shim_abi` + `shim_source_revision`: from `native/shim/identity.json`.
  The ABI version is `0.1.0` while the M3 ABI is provisional; the source
  revision starts at `1` and is incremented whenever shim source or
  identity changes (a rebuild with identical identity JSON must produce
  a token-identical binary; the binary hash may still change with the
  toolchain, which is why identity is the token, not the file hash).
- `core_version` + `core_commit`: from `native/upstream.json` (single
  provenance authority, already maintained by the pin refresh).

`native/upstream.json` gains a `shim` block: ABI version, source
revision, crate pin (version + tarball SHA-256), toolchain version at
build time, per-RID binary `binary_sha256`/`binary_bytes` + `qualified`
flag (win-x64 only flips after the green rerun; others stay absent this
slice — packaging them is M3.1b+/M3.2 territory, never pre-marked).

`eng/Build-Shim.ps1` records: cargo/rustc versions, `cargo build
--release --locked` (checksums against Cargo.lock), binary SHA-256 +
size, and updates the manifest under `native/staging/win-x64/`
(`minigrafsharp-shim.json`). The official binary staging layout is
untouched.

`native/include/minigrafsharp_shim.h` is generated with `cbindgen`
(toolchain pinned at record time) from `cbindgen.toml`; it is archived,
hash-recorded inside the `shim` provenance block, and re-diffed at every
future shim ABI change.

## M3.1a harness case list

New program-style harness `tests/MinigrafSharp.ShimTests` (plain
`Program.cs`, no xunit, following the existing conventions; NOT part of
any shipped package). It loads the shim by absolute staging path with a
plain `LibraryImport` target; the override variable is intentionally not
used here (that loader machinery is M3.1b). Cases:

1. `gate` — prints `shim qualification target:` line: core
   version/commit from `upstream.json`, shim ABI/revision from
   `identity.json`; both files parsed and required to exist.
2. `identity token byte-exact` — `mgs_shim_identity()` returns exactly
   the token composed from the two tracked JSON files.
3. `identity immutable` — repeated calls return the same pointer.
4. `negative: official binary rejected under shim identity` — offering
   the official `minigraf.dll` as the shim file must fail clearly (no
   `mgs_shim_identity` symbol; `EntryPointNotFoundException`/equivalent),
   never silently accepted.
5. `lifecycle + execute parity smoke` — `mgs_open_in_memory` + transact +
   query through `mgs_execute`; shapes `{"transacted":…}` /
   `{"variables":…,"results":…}`; every owned string freed exactly once.
6. `prepare + slot names` — `(query [:find ?n :where [?e :person/name $n]])`
   → `mgs_prepare` non-NULL, `mgs_prepared_slot_names` exactly
   `["n"]`; multiple slots sorted/dedup.
7. `prepared execute with val/entity/ref/keyword` — transact fixture;
   prepared execute returns the expected rows; re-execute with different
   bindings sees fresh state (a second transaction becomes visible).
8. `temporal slots` — `tx_count`/`timestamp`/`any_valid_time` bindings
   fill `:as-of`/`:valid-at` (#603 area: a literal window next to a slot
   is preserved — fixture asserting identical result sets against the
   equivalent unprepared query, fix verified at 2.0.7).
9. `extra bindings ignored` — upstream semantics on the shim surface.
10. `error paths` — prepare rejects `transact`/`(rule …)` (NULL +
    diagnostic); attribute slot at prepare (NULL + diagnostic);
    prepared execute missing slot (NULL + INT-033 diagnostic text
    present, no parsing for behavior); wrong kind (INT-035 text);
    malformed bindings JSON (NULL + parse diagnostic); non-finite float
    rejected as parse error.
11. `disposal` — `mgs_prepared_query_free` once, NULL-safe; db reuse
    after freeing the query; `mgs_close` once per handle; NULL-safe.
12. `cross-library file handoff (native level)` — same file written by
    the official `minigraf_execute`, handoff to `mgs_open` (and reverse)
    in both directions with content checks (ADR hard invariant 2 native
    form), sequential only.
13. `cross-library concurrent same-file open rejected (native level)` —
    official-owned file rejected by shim open and vice versa, both
    directions, both handles stay alive (ADR hard invariant 1), fixture
    under the test-root guard with recorded cleanup.

All destructive file fixtures run under a guarded temp root
(`%TEMP%\MinigrafSharp.ShimTests\<guid>\`) with an under-root assertion
and `finally` cleanup, mirroring the qualification harness precedent.

## Acceptance criteria

1. Shim builds `--release --locked` for win-x64 from the checked-in
   manifest; binary SHA-256 + size recorded in the `shim` provenance
   block and the staging manifest.
2. The 12 exports resolve; identity token is byte-exact between the
   binary and the tracked sources; the official binary is rejected with
   a clear load-failure signature.
3. Every harness case passes; no case is skipped or missing without a
   recorded, reviewed reason; `minigraf =2.0.7` checksum matching is
   enforced by `--locked`.
4. Cross-library file probes (invariants 1 and 2, native form) pass both
   directions with content checks.
5. Evidence section in `docs/verification.md`: exact revision, host, RID,
   toolchain, binary + tarball + header hashes, each case's observed
   result including any corrected probe (engine data never adjusted to
   fit), cleanup state, and explicit exclusions (no linux/osx shim build,
   no managed loader, no public API — recorded as pending, never passed).
6. No repo file outside the listed scope is modified; `MinigrafDatabase`
   and the official staging layout untouched; no v3 dependency of any
   kind.

## Explicit non-goals (M3.1a)

- No managed API, no `NativeMethods`/`SafeHandle` additions, no loader
  override variable wiring, no packaging/publication change.
- No ABI beyond the 12 exports; no structured error-code encoding; no
  checkpoint export; no write-transaction surface.
- No upstream source files copied; engine semantics adopted verbatim via
  the pinned crate and never patched.
- No macOS/Linux shim builds (recorded pending with their RID rows);
  no AOT/trimming qualification; no CI pipeline change.
- The shim remains Gagamba-independent: no Penghou/Hufu/authorization
  surface of any kind.

## Review gate

This record is the executable checkpoint definition for M3.1a. Review
before implementation: (a) the identity token schema and revision
policy, (b) the bindings JSON schema table, (c) the export list as fixed
verbatim, (d) the harness case list, (e) the two-level provenance split
(engine pin = upstream.json, shim identity = identity.json). Any change
after approval bumps `shim_source_revision` and re-records both binary
and header hashes before evidence lands.

## Execution record (M3.1a, 2026-10-17)

Executed as approved, with these recorded adjustments (full evidence in
`docs/verification.md`, "M3.1a evidence" section):

1. Case list: 13 proposed cases + 1 execution-added case
   (`official binary positive control`) = 14. No case skipped.
2. `mgs_prepared_slot_names` mechanism, as built: since the core crate
   keeps its parsed slot list `pub(crate)`, the shim derives the list by
   a token-level scan mirroring the engine lexer (strings, EDN comments,
   numbers, symbols, keywords, tagged literals verified against the
   v2.0.7 parser); a `$name` token outside those contexts is collected.
   Concession (ABI 0.1.0 behavior, harness-falsifiable): exotic syntaxes
   may cause the exposed list to differ from the engine's internal
   collection; both directions stay honest — the engine ignores
   bindings it does not reference (over-collection), and a missing bind
   surfaces the engine's INT-033 diagnostic at execute (under-
   collection). Queries the engine rejects at prepare never reach the
   scan.
3. Bindings schema: as specified; `value` of the `null` type bind is
   optional; all other kinds require `value` of the exact JSON type.
4. Identity/provenance: unchanged from this record; the shim source
   changed once during execution (string-skip scanner bug fixed by its
   dev test) and the recorded binary hash reflects the final build while
   the identity token was unchanged — exercising the token-not-hash
   policy deliberately.
5. Engine facts pinned during execution (recorded in evidence): result
   `variables` carry the `?` prefix; transact ids are unix-ms
   (`TxId = tx_id_now()`); `TxCount` slots and raw `:as-of` counters are
   the same domain (measured equal); raw `:valid-at` literals are ISO
   8601-only while `Timestamp(i64)` slots substitute directly (measured
   parity for one far-future form).
