# Verification (M0 + M1a)

Executed evidence only. All commands run from the repository root on the
development machine unless noted. M0 evidence is retained below; M1a
evidence follows it.

## Environment

- OS: Windows 10.0.26200 (x64), RID `win-x64`.
- SDK: 10.0.401 (global.json pins `10.0.401`, rollForward latestFeature).
- Runtimes: Microsoft.NETCore.App 8.0.31 / 9.0.20 / 10.0.12.
- Shell: Windows PowerShell 5.1 (`pwsh` is not installed; `eng/Get-Native.ps1`
  is 5.1-compatible).

## Native staging

`powershell -NoProfile -ExecutionPolicy Bypass -File eng/Get-Native.ps1`:

- downloaded `minigraf-c-v2.0.5-windows-x86_64.zip`;
- release SHA-256 verified: `788AF84FD82DEEE8B1065A4D4929804F1D8BC33A69836FED068EF2DCB172BE99`;
- staged `native/staging/win-x64/minigraf.dll` (2,163,712 bytes);
- binary SHA-256 verified: `C534DD26CAC77C4F505E510F9C9D647923796494D86C5B82EF2BC7609B4E21BC`;
- wrote `native/staging/win-x64/minigrafsharp-native.json`.
- `dumpbin /exports` on the staged DLL shows exactly the 7 documented
  `minigraf_*` exports.

## Restore and build

- `dotnet restore MinigrafSharp.slnx`: 4/4 projects restored.
- `dotnet build MinigrafSharp.slnx -c Release --no-restore`: succeeded,
  `net8.0` + `net10.0` for all 4 projects, 0 warnings, 0 errors
  (warnings are errors).

## Unit tests (no native required)

`dotnet run --project tests/MinigrafSharp.Tests -c Release --no-build -f <tfm>`:

- net8.0: 29/29 PASS, exit 0.
- net10.0: 29/29 PASS, exit 0.
- Covers: RID matrix (5 supported, 3 unsupported), filenames, override
  resolution, candidate order, upstream pin constants vs
  `native/upstream.json`, archived header hash, exact public surface
  (4 types, namespace `MinigrafSharp`, no `IntPtr`/`SafeHandle` leakage).

## Integration tests (real pinned engine)

`dotnet run --project tests/MinigrafSharp.IntegrationTests -c Release --no-build -f <tfm>`:

- net8.0: 11/11 PASS, exit 0.
- net10.0: 11/11 PASS, exit 0.
- Covers: missing asset fails closed (first, before any load), staged asset
  present, all 7 C exports resolvable, `OpenMemory`/`Dispose`, repeated
  dispose, 100 open/dispose cycles, 8x25 concurrent open/dispose, override
  load in a child process, `LoadedPath` diagnostics.

## QuickStart

`dotnet run --project samples/MinigrafSharp.QuickStart -c Release --no-build -f <tfm>`:

- net8.0 and net10.0: `Minigraf opened successfully.`, exit 0.

## Format

- `dotnet format MinigrafSharp.slnx --no-restore` applied (LF enforced by
  `.editorconfig`); `dotnet format MinigrafSharp.slnx --verify-no-changes
  --no-restore` passes, exit 0.

## Test runner note

M0 follows the BiscuitSharp style: plain Exe runners with no test-framework
dependency, executed per TFM via `dotnet run` (the repo-specific equivalent
of `dotnet test` allowed by the milestone spec §19). `dotnet test
MinigrafSharp.slnx` exits 0 with no test-SDK projects to discover.

## Scope confirmation

No M1 surface was implemented: no `Execute`/`ExecuteRaw`, no `Checkpoint`,
no `Open(path)`, no result model, no transactions/options/async/LINQ.
Public types are exactly `MinigrafDatabase`, `MinigrafException`,
`MinigrafOpenException`, `MinigrafNativeLoadException` (the last two are the
minimum needed for M0 failure paths).

---

# M1a evidence

Same machine, RID `win-x64`, SDK 10.0.401, staged asset unchanged from M0
(`minigraf.dll` SHA-256 `C534DD26…E21BC`).

## Build

- `dotnet build MinigrafSharp.slnx -c Release`: succeeded, `net8.0` +
  `net10.0` for all 4 projects, 0 warnings, 0 errors.

## Unit tests

`dotnet run --project tests/MinigrafSharp.Tests -c Release --no-build -f <tfm>`:

- net8.0: 29/29 PASS, exit 0.
- net10.0: 29/29 PASS, exit 0.
- Change from M0: public-surface test now expects exactly the 5 M1a types
  (adds `MinigrafExecutionException`); all other checks unchanged.

## Integration tests (real pinned engine)

`dotnet run --project tests/MinigrafSharp.IntegrationTests -c Release --no-build -f <tfm>`:

- net8.0: 27/27 PASS, exit 0.
- net10.0: 27/27 PASS, exit 0.
- M0 checks (11) all still pass, unchanged.
- New M1a checks (17): null datalog rejected (`ArgumentNullException`);
  disposed `ExecuteRaw` throws `ObjectDisposedException`; transact returns
  non-empty JSON with a `transacted` property (observed live shape, e.g.
  `{"transacted":1791768479534}` — a transaction id, not `1`); query raw
  JSON contains `Alice`; `Zürich ❄ 日本語 🙂` round-trips exactly
  (asserted on `JsonDocument`-decoded values, robust to JSON escaping);
  malformed Datalog throws `MinigrafExecutionException` with a non-empty
  native diagnostic; 50 repeated failures all diagnosed; valid call
  succeeds after failures; 20 alternating invalid/valid rounds keep the
  handle usable; 1000 mixed transact/query calls succeed (owned-string
  free on every success path); error-capture race of 8×120 alternating
  valid/invalid on one handle: 480/480 valid succeed, 480/480 failures
  diagnosed, 0 empty diagnostics, 0 unexpected outcomes.

## QuickStart

- net8.0 and net10.0: prints the raw transact JSON (e.g.
  `{"transacted":1791768568862}`), exit 0.

## Format

- `dotnet format MinigrafSharp.slnx --verify-no-changes --no-restore`
  passes, exit 0.

## Scope confirmation (M1a)

Added: `ExecuteRaw(string)`, `MinigrafExecutionException`, internal
`minigraf_execute` / `minigraf_string_free` / `minigraf_last_error`
declarations, internal strict-UTF-8 copy helper, per-database operation
gate. None of these were added: `Execute()`, typed results,
`MinigrafQueryResult`, `MinigrafRow`, `MinigrafValue`, `Checkpoint()`,
`Open(path)`, persistence, temporal helpers, prepared queries,
transactions, options, async.

---

# M1b evidence

Same machine, RID `win-x64`, SDK 10.0.401, staged asset unchanged
(`minigraf.dll` SHA-256 `C534DD26…E21BC`). Native interop untouched
(`git diff` on `Interop/NativeMethods.cs`, `Native/`, `NativeUtf8.cs` is
empty for M1b); `Execute()` is `ExecuteRaw()` plus managed decoding.

Upstream representations verified against pinned v2.0.5 sources before
coding: `TxId = u64` (`src/graph/types.rs`), `Value::Integer(i64)` /
`Float(f64)` / `Boolean(bool)`, C serialization in `minigraf-c/src/lib.rs`
(`{"transacted":…}`, `{"retracted":…}`, `{"ok":true}`,
`{"variables":…,"results":…}`, Ref/Keyword→string, non-finite float→null).

## Build

- `dotnet build MinigrafSharp.slnx -c Release`: succeeded, `net8.0` +
  `net10.0` for all 4 projects, 0 warnings, 0 errors.

## Unit tests (decoder direct, no native)

`dotnet run --project tests/MinigrafSharp.Tests -c Release --no-build -f <tfm>`:

- net8.0: 68/68 PASS, exit 0.
- net10.0: 68/68 PASS, exit 0.
- 29 retained M0/M1a checks (surface test now pins 14 types, adds
  `MinigrafResultException` + 8 model types) plus 39 new: transacted /
  retracted / ok / extra-field tolerance; query shape + positional/named
  cells; 9-scalar row (string, −5, 0, 42, 3.14, true, false, null,
  `ü❄` escapes); kind-sensitive equality; 5 wrong-kind accessor throws;
  TryGetValue hit/miss, `KeyNotFoundException`, `ArgumentOutOfRangeException`,
  null-variable rejection; `ReadOnlyCollection` immutability; 27/27
  malformed shapes rejected (`MinigrafResultException`), including
  conflicting shapes, lone `variables`/`results`, wrong id types, negative
  and overflowing ids, `ok:false`/`ok:null`, width mismatch, duplicate
  variables, nested values, integer overflow past i64.

## Integration tests (real engine via Execute())

`dotnet run --project tests/MinigrafSharp.IntegrationTests -c Release --no-build -f <tfm>`:

- net8.0: 46/46 PASS, exit 0.
- net10.0: 46/46 PASS, exit 0.
- 27 retained checks plus 19 new: transact→`MinigrafTransactionResult`;
  query rows with `Kind`/`GetString`; engine integer 30, −7, float 4.5,
  true/false all exact; `nil` transacts and decodes to `Null` (engine
  supports the literal); retract→`MinigrafRetractionResult` and the fact
  leaves the current view; rule definition→`MinigrafOkResult.Instance`;
  two-variable row positional/named agreement, miss vs `KeyNotFound`;
  `Zürich ❄ 日本語 🙂` through `Execute()`; null/disposed/malformed
  boundaries preserved distinctly (malformed Datalog is
  `MinigrafExecutionException`, never `MinigrafResultException`).

## QuickStart

- net8.0 and net10.0: prints `Alice` via typed `Execute()`, exit 0.

## Format

- `dotnet format MinigrafSharp.slnx --verify-no-changes --no-restore`
  passes, exit 0.

## Scope confirmation (M1b)

Added: `Execute(string)`, `MinigrafResult` hierarchy,
`MinigrafRow`/`MinigrafValue`/`MinigrafValueKind`,
`MinigrafResultException`, internal `MinigrafResultDecoder`. RawResult is
deliberately not attached to the exception (database values must not leak
into logs). None of these were added: `Checkpoint()`, `Open(path)`,
persistence, prepared queries, transactions, open options, async APIs.
`Ref`/`Keyword` decode as String and non-finite floats as Null, documented
as inherited C-boundary limits.
