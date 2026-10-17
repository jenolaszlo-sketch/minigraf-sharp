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

---

# M1c evidence

Same machine, RID `win-x64`, SDK 10.0.401, staged asset unchanged.
Native additions limited to `minigraf_open` and `minigraf_checkpoint`
(no existing signature touched); checkpoint contract verified from pinned
`minigraf-c/src/lib.rs` (0 = success and clears error, −1 = error with
retrievable text; open failure yields no handle and therefore no native
diagnostic).

## Build

- `dotnet build MinigrafSharp.slnx -c Release`: succeeded, `net8.0` +
  `net10.0` for all 4 projects, 0 warnings, 0 errors.

## Unit tests (no native)

`dotnet run --project tests/MinigrafSharp.Tests -c Release --no-build -f <tfm>`:

- net8.0: 70/70 PASS, exit 0.
- net10.0: 70/70 PASS, exit 0.
- 68 retained checks (surface test now pins 15 types, adds
  `MinigrafCheckpointException`) plus `Open(null)` and `Open("")`
  argument validation.

## Integration tests (real engine + real filesystem)

`dotnet run --project tests/MinigrafSharp.IntegrationTests -c Release --no-build -f <tfm>`:

- net8.0: 62/62 PASS, exit 0.
- net10.0: 62/62 PASS, exit 0.
- All M0/M1a/M1b checks retained (one pre-existing duplicated assertion
  removed). New persistence checks, each in an isolated temp directory
  with cleanup that surfaces locked-file failures: new-file creation
  (files observed beside the path: `new.db`); file-backed `Execute` and
  `ExecuteRaw`; write→checkpoint→close→reopen→read; close without
  checkpoint still persists via WAL replay on open (observed normal-close
  behavior, no crash guarantee claimed); mid-session checkpoint plus
  post-reopen read; two reopen cycles accumulating A then A+B; empty
  database reopen with empty results; `minigraf-Žluťoučký-日本語.db`
  path round-trip; missing parent and directory-as-path both fail with
  `MinigrafOpenException`; second open of an open file is rejected
  (first handle stays usable; no wrapper path lock added);
  `Checkpoint()` after dispose throws `ObjectDisposedException`;
  `Checkpoint()` on an in-memory database succeeds and the database
  stays usable; disposed databases release files (all temp dirs deleted).

## QuickStart

- Unchanged in-memory sample; net8.0 and net10.0 print `Alice`, exit 0.

## Format

- `dotnet format MinigrafSharp.slnx --verify-no-changes --no-restore`
  passes, exit 0.

## Scope confirmation (M1c)

Added: `Open(string)`, `Checkpoint()`,
`MinigrafCheckpointException`, internal `minigraf_open` /
`minigraf_checkpoint` bindings. None of these were added: crash recovery,
WAL-mismatch qualification, known-v2-issue reproductions, repair/rebuild,
backup semantics, prepared queries, transactions, open options, async
APIs. No backup correctness is claimed; recovery qualification stays in
M1d.

---

# M1d evidence

RID `win-x64`, SDK 10.0.401, engine Minigraf `2.0.5` / minigraf-c `2.0.5`,
file format `v7`. `src/` was not modified (`git diff <M1c> -- src/` empty);
M1d adds only `tests/MinigrafSharp.QualificationTests` and documentation.
No public API changed; the public-surface unit test still pins the same 15
types.

Qualification probes run the real engine and real filesystem. Destructive
probes operate only under `%TEMP%/MinigrafSharp.Qualification/<guid>/`,
guarded by a test-root assertion; recovery/corruption work happens in child
processes, never in the runner. Every probe cleans its temp directory and
surface cleanup failures. Results and classification:
[v2-qualification.md](v2-qualification.md).

## Build

- `dotnet build MinigrafSharp.slnx -c Release`: succeeded, `net8.0` +
  `net10.0` for all 5 projects, 0 warnings, 0 errors.

## Qualification runner

`dotnet run --project tests/MinigrafSharp.QualificationTests -c Release --no-build -f <tfm>`:

- net8.0: 33/33 PASS, exit 0.
- net10.0: 33/33 PASS, exit 0.

Probes and observed results (`win-x64`):

- Gate: Minigraf/minigraf-c are exactly `2.0.5` (read from
  `native/upstream.json`).
- Q1 same-tx multi-values (#371): same tx → 1 visible, split tx → 2,
  checkpoint+reopen → 2 (rebuild restores). Asserted `1 -> 2`.
- Q2 retract then re-assert: Datalog keeps the fact; the core
  `WriteTransaction` issue is not reachable through minigraf-c.
- Q3 valid-time overlap (#435): both windows visible; stable across
  checkpoint.
- Q10 temporal: window reads before/inside/after/any = 0/1/0/1;
  `:as-of 1` = first-transaction view; fixture survives checkpoint/reopen.
- Q4-control: checkpoint sequence without kill keeps A+B.
- Q11/Q11b/Q11c: point lookups (`[?e :attr literal]`) and scans work
  in-memory and after reopen (single- and multi-valued, string and
  integer). An earlier apparent miss was a malformed probe (a `:find` var
  never bound by the pattern); it was corrected, not reported as a bug.
- Q13: normal close/reopen persists; Q13b: WAL present while open and
  removed by close-time checkpoint on dispose.
- Recursion: derives transitively (2 rows); rules are session-local
  (0 rows without re-register, 2 after re-declaring).
- Q8: 100 bytes of a valid 24576-byte file → `MinigrafOpenException`
  (never a silent empty DB); all-zero 100-byte and zero-length files open
  as new empty databases.
- Q9: half-truncated WAL → opens; checkpointed generation visible,
  uncheckpointed entry lost.
- Q5 old-graph + newer-WAL (#535): accepted; newer WAL replays over the
  restored graph. Fixture manifest (name/size/SHA-256) logged.
- Q7 abrupt exit after returned write: 5/5 recovered.
- Q4 abrupt exit during write/checkpoint loop: 5/5 reopen cleanly, prior
  data survives, post-kill checkpoint never shrinks the fact set; kills
  landed 78–279 iterations into the loop. No exact in-checkpoint crash
  point is claimed.

## Regression status

- Unit `tests/MinigrafSharp.Tests`: net8/net10 PASS (unchanged).
- Integration `tests/MinigrafSharp.IntegrationTests`: net8/net10 PASS.
- QuickStart: net8/net10 print `Alice`.

## Format

- `dotnet format MinigrafSharp.slnx --verify-no-changes --no-restore`
  passes, exit 0.

## Scope confirmation (M1d)

Added: `tests/MinigrafSharp.QualificationTests` and documentation only.
None of these were added: crash-recovery APIs, WAL-mismatch handling,
known-v2-issue workarounds, repair/rebuild, backup semantics, prepared
queries, explicit transactions, open options, async APIs. No public API
changed.

---

# M2 evidence

RID `win-x64`, SDK 10.0.401, engine Minigraf `2.0.5` / minigraf-c `2.0.5`.
Package `MinigrafSharp 0.1.0-preview.1`, built from the same staged
`minigraf.dll` (SHA-256 `C534DD26…E21BC`) that qualification used.
`src/` code changed: none. The only `src/` change is packaging metadata in
`MinigrafSharp.csproj` (PackageId/Version/metadata, opt-in `IsPackable`,
native `<None>` pack items, and a pack-time asset-requirement target). No
`.cs` file changed; the public-surface unit test still pins the same 15
types.

## Package

- Built: `dotnet pack src/MinigrafSharp/MinigrafSharp.csproj -c Release
  -p:MinigrafSharpEnablePack=true -o artifacts/packages` →
  `artifacts/packages/MinigrafSharp.0.1.0-preview.1.nupkg` (910,573 bytes).
- Contents: `lib/net8.0|net10.0/MinigrafSharp.dll` (+ xml),
  `runtimes/win-x64/native/minigraf.dll`, `LICENSE`, `NOTICE`, `README.md`,
  nuspec. No forbidden payload.
- nuspec: id `MinigrafSharp`, version `0.1.0-preview.1`, license
  Apache-2.0, repository url + commit, zero external managed dependencies.

## Package verifier

`eng/Verify-Package.ps1` → PACKAGE VERIFICATION PASSED (exit 0). Checks
TFMs, RID set, native SHA-256 vs `native/upstream.json`, identity, legal
files, dependency-free, and forbidden payload.

Tamper proof: a copy of the package with one byte flipped in the packaged
`minigraf.dll` fails with exactly `win-x64 native SHA-256 matches pinned
metadata` → verifier exit 1, all other checks still passing.

## Clean consumer (`eng/Test-PackageConsumer.ps1`)

- net8.0 and net10.0: PASS — install from local feed, build, deployed
  native hash matches pinned metadata, `override=absent` at runtime, and
  in-memory + file-backed (checkpoint/reopen) output `Alice`/`Bob`.
- Negative: with the deployed native asset removed, the consumer exits
  non-zero with `MinigrafNativeLoadException` naming RID, filename and
  probed locations.
- Isolation confirmed: `MINIGRAFSHARP_NATIVE_PATH` absent, consumer under
  the OS temp root outside the repository, no `ProjectReference`, isolated
  `NUGET_PACKAGES`.

## NativeAOT (`eng/Test-AotConsumer.ps1`)

- win-x64 / net10.0 (`samples/MinigrafSharp.AotSmoke` copied outside the
  repo): PASS — publish succeeded, 0 trim/AOT analysis warnings, native
  asset deployed beside the executable, execution output `override=absent`
  + `Alice`.

## Trimming (`eng/Test-TrimmedConsumer.ps1`)

- win-x64 / net10.0: PASS — publish succeeded, 0 trim-analysis warnings,
  native asset deployed, execution output `override=absent` + `Alice`.

## Wrong-architecture diagnostic

Loading the pinned Linux `libminigraf.so` under the Windows filename via
`MINIGRAFSHARP_NATIVE_PATH` fails with `MinigrafNativeLoadException`
(RID, architecture, filename, probed locations) wrapping
`BadImageFormatException` — no crash, no fallback.

## Regression at M2

- Build: 5 projects, net8 + net10, 0 warnings / 0 errors.
- Unit: 70/70 net8 and net10. Integration: 62/62 net8 and net10.
  Qualification: 33/33 net8 and net10. QuickStart: `Alice` net8 and net10.
- `dotnet format MinigrafSharp.slnx --verify-no-changes --no-restore`: exit 0.

## Scope confirmation (M2)

Packaged `win-x64` only (at that time the only RID qualified by execution;
superseded by the preview.3 multi-RID release infrastructure below). Added
`docs/packaging.md`, `eng/` verifiers, a package-consumer script, and
packaging metadata. No new database feature API. No NuGet.org publication, no
release tag, no Minigraf v3.

---

# M2.5 evidence

Baseline `d56e021`. RID `win-x64`, SDK 10.0.401, Minigraf/minigraf-c
`2.0.5`. Summary and matrices: [hardening.md](hardening.md).

## M2 candidate identity (captured before M2.5 work)

```text
file:      artifacts/packages/MinigrafSharp.0.1.0-preview.1.nupkg
sha256:    595FCB14E10FB9A10F03689F2CC7C4662F8BD9999E74D69C621F437D0D9680DE
size:      910573
managed:   lib/net8.0 5F0280AB43B9093AA8F0CA6FDD1F105556D179DC4BA17DFEA630A9F423A42F80
           lib/net10.0 0B488A366F07183236F5C49F23653C9CA30634A37D0E5B6E4647B8B22A97A747
native:    runtimes/win-x64/native/minigraf.dll C534DD26CAC77C4F505E510F9C9D647923796494D86C5B82EF2BC7609B4E21BC
```

This candidate was **invalidated**: M2.5 reproduced a fail-closed defect in
the result decoder (a lone UTF-16 surrogate escape surfaced
`InvalidOperationException` instead of `MinigrafResultException`). The fix is
in `src/MinigrafSharp/Results/MinigrafResultDecoder.cs`; the corrected
candidate is `0.1.0-preview.2`. `src/` is otherwise unchanged from `d56e021`.

## Production diff

```text
git diff d56e021 -- src/
- MinigrafSharp.csproj: <Version> 0.1.0-preview.1 -> 0.1.0-preview.2 (packaging identity)
- Results/MinigrafResultDecoder.cs: route string reads through a helper that
  translates JsonElement.GetString() reader failures into MinigrafResultException
```

The csproj `<Version>` change is intrinsic to any content change; the decoder
change is the reproduced defect fix. No public API changed (the surface test
still pins the same 15 types; API inventory 46 members, sha256
`7D299F8EB11A1EDA218BA8DFA0E24033AEC824DBCE9A47CC9E4093003A7277D2`).

## Hardening results

- Unit runner (net8/net10): hostile corpus 34/34 rejected; edge corpus
  predictable (no unexpected exception types); bounded fuzz 500 inputs, 0
  unexpected; large 1×5000 and 5×500 and 250k-string decode; 9 UTF-8 helper
  negative/positive checks; result-privacy sentinel absent from
  `MinigrafResultException`.
- Integration runner (net8/net10): loader matrix 6/6 fail closed;
  alternation 300/300; execute soak 10,000 mixed ops (8000/2000); lifecycle
  5,000 cycles with bounded handle delta; persistence 200 accumulate/reopen
  cycles; 8 independent DBs isolated; same-handle 600 ok / 600 diagnosed / 0
  unexpected; dispose race 20 rounds, 0 unsafe; execution-error sentinel
  echoed = false.
- Qualification runner (net8/net10): unchanged, still 33/33.

## Package (corrected candidate `0.1.0-preview.2`)

- `eng/Verify-Package.ps1`: PASS (incl. new duplicate-entry check).
- `eng/Test-PackageVerifier.ps1`: control PASS; 10/10 tamper variants rejected.
- `eng/Test-PackageConsumer.ps1`: net8 + net10 install/build/deploy/output;
  missing-native fail-closed; PATH rogue ignored; cwd rogue ignored; override
  valid/nonexistent/corrupt/wrong-arch as documented.
- NativeAOT net10 publish + run (0 warnings, native deployed, plus
  missing-native negative) and trimmed net10 publish + run (0 warnings).
  These used `eng/Test-AotConsumer.ps1`/`eng/Test-TrimmedConsumer.ps1` and
  `samples/MinigrafSharp.AotSmoke` at the time; all three were folded into
  `eng/Test-PackageConsumer.ps1` (`-Aot`/`-Trim`) in the preview.3 release
  infrastructure below.

## Privacy, legal, inventory

- No automatic logging; `MinigrafResultException` retains no raw JSON.
- Package inventory clean (no unexpected content); native depends only on
  OS/CRT libraries; nuspec metadata correct with zero managed dependencies.

## Preview release decision

`PREVIEW RELEASE READY` (see the M2.5 final report). No publication performed.

---

# Release infrastructure (preview.3)

Multi-RID distribution and trusted publishing replace the local-key release
procedure. `0.1.0-preview.2` remains an unpublished win-x64-only candidate;
the new candidate is `0.1.0-preview.3`.

## Pipeline

- `.github/workflows/ci.yml`: `format` (ubuntu); `dist` (per RID on its own
  OS: `windows-latest`/win-x64, `ubuntu-latest`/linux-x64,
  `macos-latest`/osx-arm64) staging + `eng/Verify-NativeStaging.ps1` + the
  real-engine unit/integration/qualification suites, uploading
  `native/staging/<rid>`; `pack` (ubuntu) downloading the three staging
  artifacts, `eng/Verify-NativeStaging.ps1`, `dotnet pack`, `eng/Verify-Package.ps1`
  and `eng/Test-PackageVerifier.ps1`, uploading `nupkg`; `consume`
  (3 OS x net8/net10, NativeAOT on net10.0) running
  `eng/Test-PackageConsumer.ps1` from the CI-built package.
- `.github/workflows/publish.yml`: `workflow_dispatch`; validates an exact
  successful `ci.yml` push run for a release SHA (11 jobs), downloads its
  immutable `nupkg`, verifies it, and publishes via GitHub OIDC trusted
  publishing (`NuGet/login@v1`, secret `NUGET_USER`); never rebuilds.
- `.github/workflows/verify-published.yml`: installs the published version
  from nuget.org on 3 OS x net8/net10.

## Local validation performed before push (win-x64)

- `eng/Get-Native.ps1` staged all three RIDs; `eng/Verify-NativeStaging.ps1`
  passed for `linux-x64`, `osx-arm64`, `win-x64` (inventory, hashes, manifest,
  and container format/architecture: ELF x86-64, Mach-O universal, PE x86-64).
- `dotnet pack` produced `0.1.0-preview.3` containing all three RIDs;
  `eng/Verify-Package.ps1` PASS; `eng/Test-PackageVerifier.ps1` PASS
  (control + 10/10 tamper rejected).
- `eng/Test-PackageConsumer.ps1 -TargetFramework net8.0` PASS (markers +
  PATH/cwd/missing-native/override-missing/corrupt/wrong-architecture).
- `eng/Test-PackageConsumer.ps1 -TargetFramework net10.0 -Aot -Rid win-x64`
  PASS (NativeAOT publish + run, all markers).

## CI execution

CI run identity (run IDs per job), the qualified `preview.3` package
SHA-256, and the full RID/consumer/AOT matrix are recorded in the release
report for the qualifying `ci.yml` run for this commit.

---

# Maintenance checkpoint (2.0.7 pin refresh) evidence

Baseline `8710a9c` (the repository state at which this checkpoint was
executed and approved; the proposal
`docs/m3-checkpoint-1-proposal.md` and ADR
`docs/decisions/0002-prepared-queries-owned-shim.md` were reviewed
untracked at this baseline and are committed with this checkpoint). RID
`win-x64`, local host: Windows 11 (`Microsoft Windows NT 10.0.26200.0`),
Intel(R) Core(TM) Ultra 5 125H, SDK 10.0.401 (MSBuild 18.9.11). Engine
Minigraf `2.0.7` / minigraf-c `2.0.7`; staged asset
`native/staging/win-x64/minigraf.dll` SHA-256
`5FB9B8C8A0B4B7D2C37C951A36DEBF45E39D887894ADA1F753630834D390B607`
(2,150,400 bytes), verified against the digest recorded in
`native/upstream.json`.

## Pin refresh (exact identity)

- `native/upstream.json`: both pins at `2.0.7` — engine commit
  `2d2e5d75f28e91d61be9f293de895e2271e62a29`, minigraf-c commit
  `4b598c043777be51163d3a1f309e7398d060da5d`; header re-sourced from the
  upstream v2.0.7 raw URL, SHA-256
  `8603C0745EDE143BC9C231E2812E7BCDC2E9B37186CF3E99E8E8F9AE442ADE7D`,
  hash-identical to the archived v2.0.5 header (C ABI unchanged; recorded
  side by side in the header note).
- Asset digests recorded for five RIDs, all `qualified:false` at first:
  win-x64 `5FB9B8C8…`, linux-x64 `F7B63251…`, linux-arm64 `4B8BFBC5…`,
  osx-x64/osx-arm64 `2BD4F21D…` (universal2 asset).
- `eng/Get-Native.ps1` re-staged `win-x64`, `linux-x64`, `osx-arm64`
  (downloaded release archives hash-matched); each staging directory
  carries a freshly written `minigrafsharp-native.json` manifest.
- `eng/Verify-NativeStaging.ps1`: PASS (exit 0) for `linux-x64`
  (`F7B63251ED87FCBEC181CF1C0CC5DF1207DC7F74DCF24FB5556FE39B8659ED99`),
  `osx-arm64` (`2BD4F21D…16` abbreviated:
  `2BD4F21D6DDEB7EA8F19CDF9FB6467CFB4683E4EE7D0DCF2E9232A6F47B0E6C6`),
  `win-x64` (`5FB9B8C8A0B4B7D2C37C951A36DEBF45E39D887894ADA1F753630834D390B607`).

## Pin-identity updates (mechanical, no behavior change)

- `src/MinigrafSharp/Native/NativeLoader.cs`: `PinnedMinigrafVersion` /
  `PinnedMinigrafCVersion` = `2.0.7`; `PinnedMinigrafCommit` =
  `2d2e5d75…6229`; `PinnedMinigrafCCommit` = `4b598c04…5da5d`.
- `src/MinigrafSharp/MinigrafDatabase.cs`: both open-failure messages now
  cite "minigraf-c v2.0.7 exposes no error handle for failed opens".
- `src/MinigrafSharp/Interop/NativeMethods.cs`: header scope comment cites
  minigraf-c v2.0.7.
- `src/MinigrafSharp/MinigrafExceptions.cs`: `MinigrafOpenException` doc
  comment cites minigraf-c v2.0.7.
- `src/MinigrafSharp/Results/MinigrafResultDecoder.cs`: decoder doc comment
  cites the four shapes emitted by minigraf-c v2.0.7.
- No other `.cs` file changed. Public API inventory unchanged (46 members,
  sha256 `7D299F8EB11A1EDA218BA8DFA0E24033AEC824DBCE9A47CC9E4093003A7277D2`
  — printed by the unit harness).

## Qualification rerun (win-x64, v2.0.7 binaries)

Gates read the pins from `native/upstream.json`: "gate Minigraf 2.0.7",
"gate minigraf-c 2.0.7" PASS before any probe.

- Build `dotnet build MinigrafSharp.slnx -c Release`: succeeded on
  net8.0 + net10.0, 0 warnings / 0 errors.
- Qualification harness
  (`dotnet run --project tests/MinigrafSharp.QualificationTests -c Release --no-build -f <tfm>`):
  net10.0 PASS exit 0; net8.0 PASS exit 0.
- Unit harness (`tests/MinigrafSharp.Tests`): net10.0 and net8.0
  `ALL UNIT TESTS PASSED`, exit 0.
- Integration harness (`tests/MinigrafSharp.IntegrationTests`): net10.0
  and net8.0 `ALL INTEGRATION TESTS PASSED`, exit 0.
- `dotnet format MinigrafSharp.slnx --verify-no-changes --no-restore`:
  exit 0.
- New rerun probes (2.0.7 positive controls; details in the
  [v2-qualification.md `2.0.7 rerun` section](v2-qualification.md)):
  - Q-rule (#608): keyword-entity UUID normalization verified against the
    engine first (`(":a"→ref)`/`(":b"→ref)` queries normalize to `:a`/`:b`);
    then 10 upstream-mirror asserts passed, including the INT-022 case
    `(chain ?x :c)` (2 rows `:a,:b`), same-predicate bound+free calls
    keeping every edge, and unseeded `or`/`not` call shapes.
  - Q-or (#610): 8 upstream-mirror asserts passed (cross product 4 rows
    both clause orders, matched-branch 2 rows with the single `?p` value,
    no-match 0 rows, `not`-branch seeded cross product, rule-body `or`
    cross product, `:db/tx-count` pseudo-attribute join).
  - Observation method: probe contents were first taken from the exact
    upstream `magic_sets_test.rs` and `disjunction_test.rs` sources at
    v2.0.7 (fetched from the minigraf v2.0.7 tag), then asserted on the
    managed decode. Two false starts were corrected in the probe harness
    only (a row-formatting separator mismatch in the expected strings and
    one wrong expected row); the engine data was never adjusted to fit.
- Tripwires stay: #371 (same-tx multi-value), #435/#374 (valid-time
  windows), #535 (old-graph+newer-WAL) reproduce unchanged; #477 remains
  core-WriteTransaction-level and not reachable via minigraf-c Datalog;
  #554/#555/#558 remain not reachable through the wrapper's serialized
  per-database surface (per-database `_sync` gate; engine-side fixes from
  v2.0.6 release notes, adopted by the pin bump); #603 remains not
  reachable through the official C surface (no prepared-statement API in
  minigraf-c v2.0.7), core-side fix facts recorded from the v2.0.7 source
  (`src/query/datalog/prepared.rs` compared against v2.0.5 — delta is the
  #603 fix), managed recheck scheduled inside M3.1.

## Documentation updates (identity lines)

- `docs/native-boundary.md`: pinned-baseline paragraph and the two decode
  notes now cite the v2.0.7 pin/commits (header note records hash identity
  with v2.0.5).
- `docs/compatibility.md`: pinned table (v2.0.7/`2d2e5d7`/`4b598c0`);
  RID table distinguishes v2.0.7-local qualification (win-x64) from the
  prior v2.0.5-era CI qualification (linux-x64/osx-arm64 marked "rerun
  pending"); the v2.0.5 section is annotated as historical evidence; a new
  "Minigraf v2.0.7 requalification" section records observed statuses.
- `docs/hardening.md`: decoder-shape text and storage-limitation line cite
  v2.0.7 (semantics unchanged).
- `README.md`: baseline line v2.0.7, target-version note, docs index entry.
- `CHANGELOG.md`: maintenance-checkpoint entry under Unreleased.
- `docs/v2-qualification.md`: appended `v2.0.7 rerun` section (append-only;
  M1d content untouched).
- `ROADMAP.md`: intentionally NOT updated here — the file carries an
  unrelated uncommitted user edit that this checkpoint must not ride
  along with; the planned M3 sub-note is deferred to a follow-up commit.

## RID qualification status

- `win-x64`: qualified on the v2.0.7 binaries by this rerun (marked
  `qualified:true` in `native/upstream.json` after the green rerun).
- `linux-x64`, `osx-arm64`: previously CI-qualified on the v2.0.5
  binaries; the v2.0.7 rerun has NOT executed on their platforms yet —
  recorded pending, never passed.
- `linux-arm64`, `osx-x64`: unqualified (unchanged; assets hash-recorded).

## Cleanup

All destructive qualification fixtures run under a guarded temp root and
are deleted by each probe's `finally` block; every run this checkpoint used
`C:\Windows\TEMP\opencode\MinigrafSharp.Qualification\` and left it empty
after the runners exited (the harness asserts each cleanup and surfaces
failures — no cleanup failures were reported in any run).

## Scope confirmation (maintenance checkpoint)

Restricted storage, recovery, temporal or prepared-query semantics added to
`src/`: none. New public API: none. New RID qualification beyond win-x64:
none, and none recorded as passed without execution. Behavior changes
limited to the upstream-corrected query results of #608/#610 (empty/too-few
rows previously returned now return the upstream-correct rows; upstream
states stored data was never affected). No Gagamba/Penghou sibling
repository touched.
