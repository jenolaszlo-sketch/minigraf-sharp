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
of `dotnet test` allowed by the milestone spec Â§19). `dotnet test
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
(`minigraf.dll` SHA-256 `C534DD26â€¦E21BC`).

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
  `{"transacted":1791768479534}` â€” a transaction id, not `1`); query raw
  JSON contains `Alice`; `ZÃ¼rich â„ æ—¥æœ¬èªž ðŸ™‚` round-trips exactly
  (asserted on `JsonDocument`-decoded values, robust to JSON escaping);
  malformed Datalog throws `MinigrafExecutionException` with a non-empty
  native diagnostic; 50 repeated failures all diagnosed; valid call
  succeeds after failures; 20 alternating invalid/valid rounds keep the
  handle usable; 1000 mixed transact/query calls succeed (owned-string
  free on every success path); error-capture race of 8Ã—120 alternating
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
(`minigraf.dll` SHA-256 `C534DD26â€¦E21BC`). Native interop untouched
(`git diff` on `Interop/NativeMethods.cs`, `Native/`, `NativeUtf8.cs` is
empty for M1b); `Execute()` is `ExecuteRaw()` plus managed decoding.

Upstream representations verified against pinned v2.0.5 sources before
coding: `TxId = u64` (`src/graph/types.rs`), `Value::Integer(i64)` /
`Float(f64)` / `Boolean(bool)`, C serialization in `minigraf-c/src/lib.rs`
(`{"transacted":â€¦}`, `{"retracted":â€¦}`, `{"ok":true}`,
`{"variables":â€¦,"results":â€¦}`, Ref/Keywordâ†’string, non-finite floatâ†’null).

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
  cells; 9-scalar row (string, âˆ’5, 0, 42, 3.14, true, false, null,
  `Ã¼â„` escapes); kind-sensitive equality; 5 wrong-kind accessor throws;
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
- 27 retained checks plus 19 new: transactâ†’`MinigrafTransactionResult`;
  query rows with `Kind`/`GetString`; engine integer 30, âˆ’7, float 4.5,
  true/false all exact; `nil` transacts and decodes to `Null` (engine
  supports the literal); retractâ†’`MinigrafRetractionResult` and the fact
  leaves the current view; rule definitionâ†’`MinigrafOkResult.Instance`;
  two-variable row positional/named agreement, miss vs `KeyNotFound`;
  `ZÃ¼rich â„ æ—¥æœ¬èªž ðŸ™‚` through `Execute()`; null/disposed/malformed
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
`minigraf-c/src/lib.rs` (0 = success and clears error, âˆ’1 = error with
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
  `ExecuteRaw`; writeâ†’checkpointâ†’closeâ†’reopenâ†’read; close without
  checkpoint still persists via WAL replay on open (observed normal-close
  behavior, no crash guarantee claimed); mid-session checkpoint plus
  post-reopen read; two reopen cycles accumulating A then A+B; empty
  database reopen with empty results; `minigraf-Å½luÅ¥ouÄkÃ½-æ—¥æœ¬èªž.db`
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
- Q1 same-tx multi-values (#371): same tx â†’ 1 visible, split tx â†’ 2,
  checkpoint+reopen â†’ 2 (rebuild restores). Asserted `1 -> 2`.
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
- Q8: 100 bytes of a valid 24576-byte file â†’ `MinigrafOpenException`
  (never a silent empty DB); all-zero 100-byte and zero-length files open
  as new empty databases.
- Q9: half-truncated WAL â†’ opens; checkpointed generation visible,
  uncheckpointed entry lost.
- Q5 old-graph + newer-WAL (#535): accepted; newer WAL replays over the
  restored graph. Fixture manifest (name/size/SHA-256) logged.
- Q7 abrupt exit after returned write: 5/5 recovered.
- Q4 abrupt exit during write/checkpoint loop: 5/5 reopen cleanly, prior
  data survives, post-kill checkpoint never shrinks the fact set; kills
  landed 78â€“279 iterations into the loop. No exact in-checkpoint crash
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
`minigraf.dll` (SHA-256 `C534DD26â€¦E21BC`) that qualification used.
`src/` code changed: none. The only `src/` change is packaging metadata in
`MinigrafSharp.csproj` (PackageId/Version/metadata, opt-in `IsPackable`,
native `<None>` pack items, and a pack-time asset-requirement target). No
`.cs` file changed; the public-surface unit test still pins the same 15
types.

## Package

- Built: `dotnet pack src/MinigrafSharp/MinigrafSharp.csproj -c Release
  -p:MinigrafSharpEnablePack=true -o artifacts/packages` â†’
  `artifacts/packages/MinigrafSharp.0.1.0-preview.1.nupkg` (910,573 bytes).
- Contents: `lib/net8.0|net10.0/MinigrafSharp.dll` (+ xml),
  `runtimes/win-x64/native/minigraf.dll`, `LICENSE`, `NOTICE`, `README.md`,
  nuspec. No forbidden payload.
- nuspec: id `MinigrafSharp`, version `0.1.0-preview.1`, license
  Apache-2.0, repository url + commit, zero external managed dependencies.

## Package verifier

`eng/Verify-Package.ps1` â†’ PACKAGE VERIFICATION PASSED (exit 0). Checks
TFMs, RID set, native SHA-256 vs `native/upstream.json`, identity, legal
files, dependency-free, and forbidden payload.

Tamper proof: a copy of the package with one byte flipped in the packaged
`minigraf.dll` fails with exactly `win-x64 native SHA-256 matches pinned
metadata` â†’ verifier exit 1, all other checks still passing.

## Clean consumer (`eng/Test-PackageConsumer.ps1`)

- net8.0 and net10.0: PASS â€” install from local feed, build, deployed
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
  repo): PASS â€” publish succeeded, 0 trim/AOT analysis warnings, native
  asset deployed beside the executable, execution output `override=absent`
  + `Alice`.

## Trimming (`eng/Test-TrimmedConsumer.ps1`)

- win-x64 / net10.0: PASS â€” publish succeeded, 0 trim-analysis warnings,
  native asset deployed, execution output `override=absent` + `Alice`.

## Wrong-architecture diagnostic

Loading the pinned Linux `libminigraf.so` under the Windows filename via
`MINIGRAFSHARP_NATIVE_PATH` fails with `MinigrafNativeLoadException`
(RID, architecture, filename, probed locations) wrapping
`BadImageFormatException` â€” no crash, no fallback.

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
  unexpected; large 1Ã—5000 and 5Ã—500 and 250k-string decode; 9 UTF-8 helper
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

- `native/upstream.json`: both pins at `2.0.7` â€” engine commit
  `2d2e5d75f28e91d61be9f293de895e2271e62a29`, minigraf-c commit
  `4b598c043777be51163d3a1f309e7398d060da5d`; header re-sourced from the
  upstream v2.0.7 raw URL, SHA-256
  `8603C0745EDE143BC9C231E2812E7BCDC2E9B37186CF3E99E8E8F9AE442ADE7D`,
  hash-identical to the archived v2.0.5 header (C ABI unchanged; recorded
  side by side in the header note).
- Asset digests recorded for five RIDs, all `qualified:false` at first:
  win-x64 `5FB9B8C8â€¦`, linux-x64 `F7B63251â€¦`, linux-arm64 `4B8BFBC5â€¦`,
  osx-x64/osx-arm64 `2BD4F21Dâ€¦` (universal2 asset).
- `eng/Get-Native.ps1` re-staged `win-x64`, `linux-x64`, `osx-arm64`
  (downloaded release archives hash-matched); each staging directory
  carries a freshly written `minigrafsharp-native.json` manifest.
- `eng/Verify-NativeStaging.ps1`: PASS (exit 0) for `linux-x64`
  (`F7B63251ED87FCBEC181CF1C0CC5DF1207DC7F74DCF24FB5556FE39B8659ED99`),
  `osx-arm64` (`2BD4F21Dâ€¦16` abbreviated:
  `2BD4F21D6DDEB7EA8F19CDF9FB6467CFB4683E4EE7D0DCF2E9232A6F47B0E6C6`),
  `win-x64` (`5FB9B8C8A0B4B7D2C37C951A36DEBF45E39D887894ADA1F753630834D390B607`).

## Pin-identity updates (mechanical, no behavior change)

- `src/MinigrafSharp/Native/NativeLoader.cs`: `PinnedMinigrafVersion` /
  `PinnedMinigrafCVersion` = `2.0.7`; `PinnedMinigrafCommit` =
  `2d2e5d75â€¦6229`; `PinnedMinigrafCCommit` = `4b598c04â€¦5da5d`.
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
  â€” printed by the unit harness).

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
    engine first (`(":a"â†’ref)`/`(":b"â†’ref)` queries normalize to `:a`/`:b`);
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
  (`src/query/datalog/prepared.rs` compared against v2.0.5 â€” delta is the
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
- `ROADMAP.md`: intentionally NOT updated here â€” the file carries an
  unrelated uncommitted user edit that this checkpoint must not ride
  along with; the planned M3 sub-note is deferred to a follow-up commit.

## RID qualification status

- `win-x64`: qualified on the v2.0.7 binaries by this rerun (marked
  `qualified:true` in `native/upstream.json` after the green rerun).
- `linux-x64`, `osx-arm64`: previously CI-qualified on the v2.0.5
  binaries; the v2.0.7 rerun has NOT executed on their platforms yet â€”
  recorded pending, never passed.
- `linux-arm64`, `osx-x64`: unqualified (unchanged; assets hash-recorded).

## Cleanup

All destructive qualification fixtures run under a guarded temp root and
are deleted by each probe's `finally` block; every run this checkpoint used
`C:\Windows\TEMP\opencode\MinigrafSharp.Qualification\` and left it empty
after the runners exited (the harness asserts each cleanup and surfaces
failures â€” no cleanup failures were reported in any run).

## Scope confirmation (maintenance checkpoint)

Restricted storage, recovery, temporal or prepared-query semantics added to
`src/`: none. New public API: none. New RID qualification beyond win-x64:
none, and none recorded as passed without execution. Behavior changes
limited to the upstream-corrected query results of #608/#610 (empty/too-few
rows previously returned now return the upstream-correct rows; upstream
states stored data was never affected). No Gagamba/Penghou sibling
repository touched.

# M3.1a evidence (owned prepared-query shim ABI 0.1.0, win-x64)

Baseline `4447033` (the 2.0.7 pin-refresh checkpoint). RID `win-x64`, same
local host as the preceding checkpoint (Windows 11
`Microsoft Windows NT 10.0.26200.0`, Intel(R) Core(TM) Ultra 5 125H), SDK
10.0.401. Executed per `docs/m3-1a-shim-abi-proposal.md` (approval basis
ADR 0002, updated in place with an "Execution record" section below).
Native toolchain installed this slice, versions recorded at build time:
rustc `1.99.1 (58cf8f9c3 2026-10-13)`, cargo `1.99.1 (5f94df478
2026-08-27)`, cbindgen `0.29.4`, MSVC Build Tools VC `14.51.36231` +
Windows SDK 10. Managed harnesses ran under both `net8.0` and `net10.0`.

## Provenance (exact)

- Owned shim sources: `native/shim/` (Cargo.toml, identity.json, build.rs,
  cbindgen.toml committed, src/lib.rs, Cargo.lock committed with 54 pinned
  crates.io checksums; `--release --locked` enforced by `eng/Build-Shim.ps1`).
- Core crate pin: crates.io `minigraf = "=2.0.7"`, tarball SHA-256
  `74E6724A59FECE46F0F56C3FE16F1792B34E34FE08FDFF4C902B22696D760D68`
  (1,443,572 bytes); contents inspected directly (prepared.rs BindValue
  variants, db.rs prepare/execute, parser.rs lexer, types.rs TxId, error.rs
  templates).
- Parity reference (never copied): upstream minigraf-c v2.0.7 `src/lib.rs`
  fetched to the scratch area as the serialization-shape fixture source,
  SHA-256 `B14CA2F167B989309310A5052B76E30E9072A6534FD5A22A1DF52F767E8B65B9`.
- Identity token embedded in the binary (static, NUL-terminated, measured
  twice, pointer-stable):
  `{"shim_abi":"0.1.0","shim_source_revision":1,"core_version":"2.0.7","core_commit":"2d2e5d75f28e91d61be9f293de895e2271e62a29"}`
  — byte-exact against `identity.json` + `native/upstream.json` (harness
  case 2); the identity JSON was never edited after the first build, so
  token identity held while the binary was rebuilt (case 6 below).
- Binary: `native/staging/win-x64/minigrafsharp_shim.dll` SHA-256
  `CAC3D42DA456C25CC57FA24A4EF7C10FF187EBB79049D916770CA6D1BB3D6641`
  (2,232,320 bytes); recorded in `native/upstream.json` shim block (2,232,320)
  and staging manifest; `qualified` flipped only after the green rerun below.
- Header: `native/include/minigrafsharp_shim.h` regenerated by cbindgen,
  UTF-8 (validated), LF-only (enforced by the build script), SHA-256
  `0C288ABF013CDEED2F64F99C67C3A8F0F16A67EF127FAF3980E1038F9433A9E8`
  (2,988 bytes), identical across both builds of this slice.
- Export table (dumpbin, 12 symbols): mgs_shim_identity, mgs_open,
  mgs_open_in_memory, mgs_close, mgs_string_free, mgs_last_error,
  mgs_prepared_last_error, mgs_execute, mgs_prepare,
  mgs_prepared_slot_names, mgs_prepared_execute, mgs_prepared_query_free.

## Harness run (tests/MinigrafSharp.ShimTests, 14 cases)

Case-list amendment: the proposal listed 13 cases; execution added the
official-binary positive control (official minigraf.dll executes the same
fixture inside the same harness process, ruling out an environment-wide
load failure masquerading as the identity gate). The proposal record
below reflects the executed list.

- `dotnet run -f net8.0`: `pass=14 fail=0`, `ALL 14 SHIM TESTS PASSED`.
- `dotnet run -f net10.0`: `pass=14 fail=0`, `ALL 14 SHIM TESTS PASSED`.
- `dotnet format --verify-no-changes`: exit 0 (no formatting drift).
- `cargo test` (shim internal scanner unit tests, 7 cases): 7/7 passed.
- Every run printed `cleanup: complete=true`; all file fixtures lived
  under `%TEMP%\MinigrafSharp.ShimTests\<guid>\` with the under-root
  guard; no cleanup failures.

Observed engine facts that reshaped assertions (engine data never
adjusted; fixture expectations corrected against measured behavior):

1. Result JSON `variables` entries keep the upstream variable-name prefix
   (`"?n"` for `?n`) — case 5 asserted the observed shape.
2. Transact results carry `{"transacted": <unix-ms>}` — `TxId` is the
   wall-clock transaction id (`tx_id_now`, core `graph/types.rs`), not a
   small monotonic integer. In-memory fixtures needing distinct ids loop
   over fresh databases until wall-clock separation, and the `:as-of`
   counter-domain facts were pinned by equality against the equivalent
   raw `:as-of N` literal forms (`TxCount(1)`/`TxCount(2)` bindings vs raw
   `:as-of 1`/`:as-of 2`).
3. `:valid-at` raw literals accept only ISO 8601 strings or
   `:any-valid-time` (`[PRS-018]` observed); the `Timestamp(i64)` slot
   substitution bypasses raw parsing, and the equality
   Timestamp-slot(ms) == ISO-literal form was measured for
   `9999-12-31T23:59:59Z` ⇔ `253402300799000` (`#603` literal-keep
   territory: the prepared mixed clause kept the literal `:as-of` while
   substituting only the `:valid-at` slot).
4. The shim's slot-name emitter had a genuine bug found during execution
   (string contents were not skipped: the string-skip consumed the
   OPENING quote as the terminator). Fixed; shim sources changed after
   the first recorded binary, so the binary hash above reflects the final
   build while `identity.json` (and thus the identity token) was
   unchanged — recorded as the token-not-hash policy working as designed.

Observed diagnostics (behavior never parsed; presence-checked only):
missing slot → `[INT-033] missing bind value for slot '$age'`; wrong kind
→ `[INT-035] slot '$age' in value position requires Val, got
AnyValidTime`; malformed bindings JSON → `invalid bindings JSON: …`;
attribute slots and `transact`-style commands are rejected at prepare with
diagnostics on the database handle.

Cross-library invariants at the native level (sequential only):
official-written file handed off to `mgs_open` with content check, and
reverse; concurrent same-file open rejected in BOTH directions while the
owning handle stayed alive, sequential reopened after close (hard
invariants 1 and 2, native form).

## Scope confirmation (M3.1a)

New managed API: none (the harness is a test-only program; `NativeMethods`
and `MinigrafDatabase` untouched). Loader integration and the
`MINIGRAFSHARP_*` override family: untouched (resolve via test-only
DllImport resolver inside the harness). Packaging/publish: no changes; no
PR/pipeline changes. RIDs beyond win-x64: no shim binaries built —
linux-x64/osx-x64/osx-arm64 remain pending, never recorded as qualified.
No Gagamba/Penghou sibling repository touched; kept independently usable.
# M3.1b evidence (managed prepared-query surface, win-x64)

Baseline: M3.1a native anchor commit `480471e` (kept untouched; no native
change was needed and none was made in this slice). RID `win-x64`, same
local host as the preceding checkpoint (Windows 11
`Microsoft Windows NT 10.0.26200.0`, Intel(R) Core(TM) Ultra 5 125H), SDK
10.0.401. Target: `net8.0` and `net10.0` managed surface over the qualified
shim ABI 0.1.0 (identity token unchanged, byte-exact, from M3.1a:
`{"shim_abi":"0.1.0","shim_source_revision":1,"core_version":"2.0.7","core_commit":"2d2e5d75f28e91d61be9f293de895e2271e62a29"}`).

## Pre-implementation fix recorded (upstream.json BOM)

The M3.1a build-script rewrite had left `native/upstream.json` UTF-8
encoded with a BOM (PowerShell 5.1 `Set-Content -Encoding UTF8`). The
existing unit harness (`tests/MinigrafSharp.Tests`) parses that file by raw
bytes and crashed, which surfaced on this slice's first full-suite run
(the file had not been re-read by that harness after
`480471e`). Fixed: BOM stripped from the tracked file and
`eng/Build-Shim.ps1` switched to BOM-less UTF-8 writes
(`Write-Utf8NoBom`), so the regression class cannot recur. This is a
provenance-tooling fix, not a benchmark of any native behavior.

## Managed surface

Implemented per `docs/m3-1b-prepared-managed-proposal.md` (mandate of
2026-10-17; ADR 0002 basis): `MinigrafPreparedDatabase`,
`MinigrafPreparedQuery`, `MinigrafBindValue`/`MinigrafBindKind`;
`ShimNativeLoader` (`MINIGRAFSHARP_SHIM_NATIVE_PATH`, positive identity
verification before any handle is returned, failed loads never cached) with
one combined assembly resolver routing strictly by module name (official
`minigraf` to `NativeLoader`, `minigrafsharp_shim` to the shim loader, no
substitution in either direction); `ShimNativeMethods` (the closed
12-export surface); per-database gate with registration/cascade ownership;
formal `MinigrafResultDecoder` reuse (no second decoder). No raw native
handle is exposed publicly (harness-checked: unit surface test extended to
assert the exact recorded public-type closure is now "M1c surface plus
M3.1b provisional prepared-query surface" and that no
`IntPtr`/`SafeHandle` appears in any public signature).

## Harness run (tests/MinigrafSharp.PreparedQualificationTests, 26 cases)

- net8.0: `pass=26 fail=0`, `ALL 26 PREPARED-QUALIFICATION TESTS PASSED`.
- net10.0: `pass=26 fail=0`, `ALL 26 PREPARED-QUALIFICATION TESTS PASSED`.
- `dotnet format --verify-no-changes`: exit 0.
- `dotnet build MinigrafSharp.slnx -c Release`: 0 warnings, 0 errors.
- cleanup line: `complete=true` on every run; all fixtures under
  `%TEMP%\MinigrafSharp.PreparedQualificationTests\<guid>\` with the
  under-root guard.

Loader/identity findings (cases 1-3): the pinned managed token composes
byte-exact from the staged provenance files; the official binary offered
under `MINIGRAFSHARP_SHIM_NATIVE_PATH` is refused with
`MinigrafNativeLoadException` naming the identity mechanism ("The loaded
asset exposes no mgs_shim_identity export; it is not the owned
MinigrafSharp shim"), and the same process continues normally afterward,
demonstrating that the declared identity token - not binary content - is
the compatibility anchor (each first-shim-load case order documented in
the harness header comment: refusal probes run before any successful
cached load).

Observed diagnostics (presence-checked only, never parsed for behavior):
missing slot -> `[INT-033] missing bind value for slot '$age'`; wrong
kind -> `[INT-035] slot '$age' in value position requires Val, got
Timestamp` and `... got AnyValidTime`; raw integer `:valid-at` literal ->
`[PRS-018] :valid-at must be an ISO 8601 string or :any-valid-time, got
Integer(1000000)` while the `Timestamp(i64)` slot substitution stayed
accepted and equal to the equivalent raw ISO literal (deep-past
`1970-01-01T00:16:40Z`, far-future `9999-12-31T23:59:59Z` =
`253402300799000`). Engine tx ids observed as wall-clock milliseconds
(id1=1792217819289, id2=1792217819309) with `Transaction(1)`,
`Transaction(2)` and `Transaction(txId)` slot rows equal to the raw
`:as-of` forms.

Ownership/disposal (cases: disposal, cascade, double disposal, use-after-
dispose, prepare failure cleanup, execution failure cleanup): explicit
query dispose frees its native state exactly once under the parent gate;
parent dispose cascade-closes registered open children first and rejects
their use afterwards with `ObjectDisposedException`; both disposal orders
and post-cascade child disposal are harmless; failed prepares and failed
prepared executes leave the query and database fully reusable.

Concurrency (cases: concurrent one-database, no-shared-gate,
two-databases-parallel): interleaved prepared reads and raw writes on one
database through the managed API produced correct rows with no
corruption; holding one database's gate and operating on a second
database succeeded (no global managed serialization gate); four parallel
workers across two databases completed without error. Concurrency claims
are limited to the serialized per-database gates; no stronger shim
guarantee is claimed.

Parity (case: raw-vs-prepared parity): identical fixtures through
`MinigrafDatabase` and `MinigrafPreparedDatabase` produce byte-identical
`ExecuteRaw` query JSON, identical decoded variables/rows/values through
the shared decoder, and prepared-with-binding rows equal to the raw
equivalent (`:age 30` literal). All seven binding kinds (entity, val with
all five payload kinds, ValRef, ValKeyword, Transaction, Timestamp,
AnyValidTime) exercised with value comparisons; extra/unknown bindings
kept their measured ignore semantics; keyword text passes through
unmodified (colon-prefixed text matches, mismatched keywords stay empty).

File-backed (case: file reopen): transact via the shim, dispose, reopen,
WAL replay visible to a prepared query, then the same file read by the
official library (hard invariant 2 sequential handoff re-proven managed-
side). M3.1a's native bidirectional concurrent-rejection evidence is cited
for hard invariant 1; no new cross-engine concurrent case was invented
here.

## Scope confirmation (M3.1b)

New public API: `MinigrafPreparedDatabase`, `MinigrafPreparedQuery`,
`MinigrafBindValue`, `MinigrafBindKind` (provisional, ADR 0002). No
`MinigrafDatabase` member changed; no packaging/NuGet change (shim package
distribution is a later dispatch, recorded pending, never passed); no RID
expansion (shim binaries for linux-x64/osx-* remain unbuilt; managed
results on win-x64 must not be read as cross-RID qualification);
integration harness child-probe invocation note: the child loader probe
requires the apphost (`Environment.ProcessPath`) as the executable;
run harnesses through the apphost binary or `dotnet run`, not
`dotnet <dll>`, and the child stderr/stdout capture was added to the
integration harness for future diagnostics. `ROADMAP.md` untouched. No
Gagamba/Penghou sibling repository touched.

# M3.1c evidence (shim packaging and multi-RID qualification, win-x64/linux-x64/osx-arm64)

Source revision: `6b9604393417db6406f07af04b0f3c97e5001f5b` (the qualified
pipeline revision; supersedes the mechanics/fix revisions
`01947ec`/`a8bcb48`/`859268a`/`2a91783`). CI run:
https://github.com/jenolaszlo-sketch/minigraf-sharp/actions/runs/39038565443
(`ci`, conclusion `success`). Baseline for provenance: M3.1a anchor
`480471e` and M3.1b `42a7269`, unchanged by this slice.

## What was executed

All eleven CI jobs passed on the source revision above: `format`; `dist`
on `windows-latest`/`ubuntu-latest`/`macos-latest`; `pack`; and six
`consume` jobs (Windows/Linux/macOS x net8.0/net10.0, NativeAOT on
net10.0). Each `dist` job:

1. staged and hash-verified the pinned official engine asset;
2. ran the real-engine unit, integration and storage/recovery suites on
   net8.0 and net10.0;
3. built the owned shim for that host with the pinned toolchain family
   (`cbindgen 0.29.4`, Rust stable), which includes the `cargo --locked`
   unit tests (7/7) and the byte-exact ABI-header pin gate;
4. verified the complete staged inventory (engine + shim + both
   manifests) including hashes, formats and architectures;
5. ran the shim harness (14/14) and the managed prepared-query harness
   (26/26) on net8.0 and net10.0.

The `pack` job assembled the package from the three exact `dist` staging
artifacts, `eng/Verify-Package.ps1` verified both native entries per RID
against the staged qualification manifests, and the verifier self-test
(14 tamper cases, including shim removal/byte-corruption/engine-
substitution) passed. The `consume` jobs installed the built package from
a clean external consumer, ran the prepared-query path plus all engine
markers, verified the deployed engine against its pinned hash and the
deployed shim against the package entry, and re-ran the shim-override
matrix (missing/corrupt/wrong-architecture), all fail-closed.

## Qualified artifacts (evidence-bound)

Package: `MinigrafSharp.0.1.0-preview.3.nupkg`, SHA-256
`A5435392ACFD9953493C3B4362469D46FCF27D7E81669A1A9D349E9A40BC04B7`
(recorded by the `pack` job; the downloaded artifact matches).

| RID | Engine SHA-256 | Shim SHA-256 | Shim bytes |
| --- | --- | --- | --- |
| `win-x64` | `5FB9B8C8A0B4B7D2C37C951A36DEBF45E39D887894ADA1F753630834D390B607` | `DB9EBAACF482F84B174562AF12A363A3D6A77850E947E1DD873834C9F2682F5F` | 2232832 |
| `linux-x64` | `F7B63251ED87FCBEC181CF1C0CC5DF1207DC7F74DCF24FB5556FE39B8659ED99` | `B8A82B10EC13D679A1065DEA06F55078E64B07D389138CD5CBF075BB68CDABA2` | 2919000 |
| `osx-arm64` | `2BD4F21D6DDEB7EA8F19CDF9FB6467CFB4683E4EE7D0DCF2E9232A6F47B0E6C6` | `D42C47D64BFC431E911D91DDCFDFAE0DD559BDDA5C5764415DD30394BABC6C16` | 2496096 |

The shim hashes are the `dist`-built artifacts (recorded in
`native/upstream.json` `shim.assets`, `qualified: true`, with the
per-RID Rust/cbindgen versions). Locally built shims are not
byte-reproducible across linker timestamps/toolchains; the chain of
custody that is verified within a run is staged binary -> staged
qualification manifest -> package entry -> deployed file, and the recorded
hashes identify the exact qualified artifacts.

## Local (win-x64) execution

On the development host, the same revision was executed locally: solution
build clean (0 warnings), `dotnet format --verify-no-changes` clean, all
five suites green on net8.0 and net10.0 (unit, integration,
qualification, shim 14/14, prepared 26/26), staging verification green,
and the packaged-consumer gate green on net8.0 and net10.0 against a
package assembled from the CI dist artifacts (prepared marker present,
engine/shim deployment chain verified, all six override probes fail
closed).

## Defects found and fixed in this slice

1. The M3.1b managed loader registered its cached shim handle but the
   assembly DllImport resolver was only registered by the official
   loader path; on POSIX a shim-only process failed every P/Invoke until
   the official path ran (Windows masked it). Fixed by registering the
   shared resolver from the shim load path (idempotent, shim->official
   lock ordering). This is a managed defect in the M3.1b surface, fixed
   here; native ABI and M3.1a behavior are unchanged.
2. `Verify-NativeStaging.ps1`'s thin Mach-O magic compare used
   PowerShell's negative-Int32 hex literal and rejected real 64-bit
   arm64 dylibs (latent: the engine asset is universal). Fixed with a
   positive decimal literal; verified on the CI macOS runner.
3. cbindgen emitted CRLF on the Windows runner; the header pipeline now
   canonicalizes to LF before the byte-exact pin gate (pinned identity is
   the LF blob).
4. The consumer probe used an invalid three-argument `Join-Path` and
   cleanup could mask the primary failure; fixed, with bounded cleanup
   retries and an explicit `cleanup: incomplete` line instead of
   replacing the failure.

## Scope confirmation (M3.1c)

Package RID set unchanged (`win-x64`, `linux-x64`, `osx-arm64`); managed
API unchanged since M3.1b; no publishing dispatch; `ROADMAP.md` untouched;
no Gagamba/Penghou sibling changes. Unqualified RIDs (`linux-arm64`,
`osx-x64`) remain unbuilt and unqualified, never recorded as passed.
Shim provenance is `shim.assets.<rid>` records plus the per-RID
toolchain versions in `native/upstream.json`; the runs above are the
executed qualification for all three RIDs.