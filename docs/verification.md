# Verification (M0)

Executed evidence only. All commands run from the repository root on the
development machine unless noted.

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
