# M3.2g - Integrated qualification (evidence)

Status: **integrated scenarios executed and qualified locally on win-x64
(net8.0/net10.0); cross-RID CI binding appended below**. This is a
qualification-only slice: no new capability, no ABI addition, no
public-surface change, no publication. It composes the independently
qualified M3.1 and M3.2a-M3.2f capabilities and proves they hold together.

Anchors: M3.2f qualified source `e92f17d`, evidence `ac229e2`,
qualification CI run 39091125054. Baseline M3.1 release:
`0.1.0-preview.4`, qualified source `418e9fc`.

## 1. What is integrated

Real native implementations only. Every case builds actual engine state
and reads it back; there are no mocks, no invocation counters, and no
managed-only assertions standing in for native behavior. The composed
surface under test:

- `MinigrafPreparedDatabase` (M3.1 prepared queries + M3.2 rich surface),
- `MinigrafOpenOptions` / `MinigrafSyncMode` (M3.2c),
- `MinigrafWriteTransaction` (M3.2b),
- structured `NativeCategory`/`NativeCode` (M3.2d),
- typed `MinigrafValueKind.Ref`/`Keyword` (M3.2e),
- `MinigrafRuntimeIdentity` (M3.2f).

New harness: `tests/MinigrafSharp.IntegratedQualificationTests`
(13 cases), wired into `MinigrafSharp.slnx` and `eng/Test-Dist.ps1`
(`integrated <tfm>`).

## 2. Five integrated scenarios

| Scenario | Behavior proven | Observed |
| --- | --- | --- |
| A. Commit and query | Open a file database with options (`Synchronous.Normal`, `WalCheckpointThreshold.Unlimited`) -> begin -> two staged batches -> commit -> prepared query -> exact values; close/reopen | 2 committed rows with `Alice=30`, `Bob=25`; WAL present after close; 2 rows after reopen (WAL replay) |
| B. Rollback | Open -> begin -> write -> transactional read sees staging -> rollback -> absent; database still usable; reopen confirms | 1 staged row inside tx; 0 after rollback; later commit persists 1 row |
| C. Failure recovery | Begin -> valid stage -> invalid stage (Parser `PRS-*`) -> valid stage -> commit -> actual state | Structured `Parser`/`PRS-*`; exactly the 2 valid stages committed |
| D. Typed values | Write Ref/Keyword -> typed decode -> bind decoded values through prepared queries -> identity/equality | `Ref` `550e8400-...-0001`, `Keyword` `:Status/Active`; `ValRef` selects the row; `ValKeyword` binds and returns the typed entity ref; equal refs hash equal |
| E. Identity refusal | Supply the official engine as the wrong binary -> refuse before use on every open path -> load the verified shim -> execute | All four open entry points (`OpenMemory`, `OpenMemory(options)`, `Open(path)`, `Open(path, options)`) plus `MinigrafRuntimeIdentity.Current` refused with `MinigrafNativeLoadException`; verified shim then opens and executes; identity `0.1.0` over `2.0.7` |

Scenario E/I9 is deliberately the first case in the process: a successful
shim load is cached for process lifetime, so the refusal is only
observable before any verified load. The failed candidates cache nothing,
which is what lets the same process then load the real shim.

## 3. Cross-feature invariants

| # | Invariant | Case result |
| --- | --- | --- |
| I1 | Write-transaction guard is thread-local, including across separate databases on the same thread | Second database `BeginWrite` refused while the first is open; works after terminalization |
| I2 | Transaction disposal rolls back without committing | Disposed staging absent, durably, across reopen |
| I3 | Prepared queries inside an open transaction see committed facts only | Prepared execute returns 1 while the transaction holds 1 staged + 1 committed |
| I4 | Transactional reads see staged and committed facts | Transactional read returns 2 |
| I5 | Rule registration is not transactional | Rule registered in a rolled-back transaction persists and sees committed facts only |
| I6 | Failed staging preserves the transaction's usable state | Valid stages before and after the failure both commit; the failed stage applies nothing |
| I7 | Failed commit retains structured diagnostics after native terminalization | `Internal`/`INT-000` with diagnostic retained; transaction terminal; nothing applied; database reusable |
| I8 | Typed Ref/Keyword never become ordinary strings on the owned surface | Typed envelopes emitted; kinds `Ref`/`Keyword`; `GetString` throws |
| I9 | Native identity verification always precedes database use | All four open entry points refused under the wrong binary (scenario E) |

No combination produced a contradiction: each individually measured
behavior holds unchanged when the capabilities are used together.

## 4. Full qualification matrix

Host matrix (local win-x64 executed; win-x64/linux-x64/osx-arm64 executed
in CI):

| Platform | .NET 8 | .NET 10 | NativeAOT |
| --- | --- | --- | --- |
| win-x64 | required / green locally, CI-bound below | required / green locally, CI-bound below | CI consumer |
| linux-x64 | CI-bound below | CI-bound below | CI consumer |
| osx-arm64 | CI-bound below | CI-bound below | CI consumer |

Reruns in the full matrix (both TFMs, per qualified RID in CI): unit
(24-type public-surface closure), integration, qualification, shim 26/26,
prepared 26/26, transactions 18/18, options 11/11, structured errors
19/19, typed values 15/15, runtime identity 14/14, integrated 13/13;
Rust 14 unit tests + 18 characterization probes; package verification,
tamper checks and clean package consumers.

Local win-x64 result (this revision): all suites green on net8.0 and
net10.0; solution builds 0 warnings; `dotnet format --verify-no-changes`
clean; every harness prints `cleanup: complete=true`.

## 5. Reproducibility and artifacts

The existing rule is preserved: linux-x64 and osx-arm64 rebuild
byte-identically; win-x64 differs only by PE linker timestamps and makes
no byte-identical claim. M3.2g changes no native source, so the shim ABI,
the generated header pin (`38B7FEE0...BA92`, 6745 bytes) and the
22-export inventory are unchanged from M3.2f. Evidence-bound shim and
engine hashes are re-recorded per RID for the M3.2g CI run below.

## 6. Cleanup

- Harness cleanup printed `cleanup: complete=true` on both TFMs.
- The forced commit-failure case removes its WAL blocker directory in a
  `finally`; no fixture survives a passing run.
- The refusal probe leaves no file (the loader refuses before any native
  open) and restores `MINIGRAFSHARP_SHIM_NATIVE_PATH` in a `finally`.

## 7. Limitations preserved (not re-qualified here)

- Rules registered inside a transaction are not transactional (measured,
  documented as a contract; I5).
- Prepared queries inside an open transaction see committed facts only
  (measured; I3).
- `Synchronous.Normal` accept/reject only; no durability claim under OS
  crash/power loss.
- `linux-arm64` and `osx-x64` remain unqualified.
