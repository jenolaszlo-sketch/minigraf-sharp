# M3.2c - Typed open options and structured open failures (evidence)

Status: implemented and locally qualified on win-x64 (net8.0/net10.0);
cross-RID CI qualification recorded below/after the slice commit. Basis:
ADR 0003 (accepted, amendment 3) and the M3.2a option characterization.

## ABI additions (2 exports; total shim inventory 22)

```c
MgsDb *mgs_open_with_options(const char *path, const char *options_json,
                             char **error_json_out);
MgsDb *mgs_open_in_memory_with_options(const char *options_json,
                                       char **error_json_out);
```

- `error_json_out` (amendment 3): on failure the function returns NULL and,
  when the out-pointer is non-NULL, stores an owned structured-error JSON
  string (`{"category","code","message"}`) that the caller frees with
  `mgs_string_free`. On success it stores NULL. A NULL out-pointer is
  tolerated (diagnostics unavailable; the call still fails closed).
- Closed schema (absent fields = engine defaults); unknown keys, wrong
  types, out-of-range values and unknown `synchronous` strings are refused
  with `Shim/MGS-003` and never normalized:

| JSON key | Type | Engine default | Qualified meaning |
| --- | --- | --- | --- |
| `wal_checkpoint_threshold` | u64 (≤ usize::MAX) | 1000 | WAL entries before auto-checkpoint; `18446744073709551615` (= `usize::MAX` on 64-bit) disables implicit checkpoints |
| `max_derived_facts` | u64 | 1_000_000 | per-iteration derivation cap |
| `max_results` | u64 | 1_000_000 | per-query derivation cap |
| `synchronous` | `"full"`/`"normal"` | `full` | WAL durability mode (accept/reject only; durability window not claimed this milestone) |

Not exposed (recorded reasons): `page_cache_size` (no externally
observable effect), `allow_unlocked` (not exercisable on qualified hosts).

Header: regenerated with cbindgen 0.29.4 and explicitly repinned as an
ABI-change (`shim.header.sha256
476F9C3FCABE89D271AA88DAAF78980F44916C653E0F79778931A6295541B891`,
6466 bytes). Local win-x64 staged shim after the change:
`sha256 8D78353E99DA551DC32383B97BBC2362BB69FF125510388471D6B1C09A417EF9`,
2278400 bytes; `native/upstream.json` refreshed by `eng/Build-Shim.ps1`
(qualified flags untouched; CI re-records per-RID hashes in the slice
evidence).

## Managed surface

- `MinigrafOpenOptions` (immutable): `Default`, `Unlimited` sentinel,
  `WalCheckpointThreshold` / `MaxDerivedFacts` / `MaxResults` (`ulong?`,
  null = engine default), `Synchronous` (`MinigrafSyncMode?`). Serialized
  to the closed JSON schema with `Utf8JsonWriter` (AOT-safe); undefined
  enum values are rejected with `ArgumentException` before any native
  call.
- `MinigrafSyncMode`: `Full`/`Normal`.
- `MinigrafPreparedDatabase.Open(string, MinigrafOpenOptions)` and
  `OpenMemory(MinigrafOpenOptions)`. The existing parameterless overloads
  are untouched and still call the classic `mgs_open`/
  `mgs_open_in_memory` exports, so default-open behavior is preserved by
  construction (and verified by qualification).
- Open failures raise the existing `MinigrafOpenException`; when the
  engine reported structured information it is carried on
  `NativeCategory`/`NativeCode`, and the diagnostic text is preserved.

## Qualification executed (local win-x64)

- Shim harness 24/24 (4 new open-option ABI cases: valid schema with
  observable threshold behavior, closed-schema refusals as
  `Shim/MGS-003`, structured out-parameter failure with preserved text,
  in-memory options and NULL out-pointer tolerance).
- New `tests/MinigrafSharp.OpenOptionsQualificationTests`: 11/11 on
  net8.0 and net10.0 (defaults preserved; threshold=1 checkpoints per
  commit; `Unlimited` defers persistence to WAL replay and reopens
  correctly; `max_results`/`max_derived_facts` enforcement observed as
  the measured `INT-020` diagnostic; Full/Normal accepted; invalid enum,
  null and empty arguments rejected before native use; in-memory WAL
  options are no-ops; structured open failure preserves category/code
  and text; plain default open unaffected by prior options use).
- Dedicated M3.2b rerun: transaction harness 18/18 on both TFMs, shim
  suite, Rust probes 18/18.
- Full M3.1 matrix rerun: unit (public-surface closure now 23 types,
  including the two M3.2c types), integration, qualification, shim,
  prepared 26/26, all green on both TFMs; solution builds 0 warnings;
  `dotnet format --verify-no-changes` clean.
- Cleanup: options harness prints `cleanup: complete=true`; fixtures use
  disposable temp directories and remove blockers; no fixture survives a
  passing run.

## Measured notes and boundaries

- Option enforcement limits surface as `Internal/INT-020` (measured
  taxonomy). Structured exception properties on the non-transaction
  `Execute` paths (e.g. limit errors) are wired in M3.2d; this slice
  asserts the measured diagnostic text for those probes and does not
  claim structured classification there. Open failures and transaction
  failures do carry structured information now.
- `Synchronous=Normal` is accepted and round-trips, but no durability
  claim is made (no OS-crash/power-loss qualification this milestone).
- Package inventory is unchanged (no new files); only the shim binaries
  change, with per-RID hashes regenerated through the pipeline.
- Unqualified RIDs (`linux-arm64`, `osx-x64`) remain unqualified.

## Cross-RID CI qualification (to be appended)

CI results for the slice commit are appended to this file after the
pipeline run, including per-RID shim hashes and the downloaded-artifact
re-verification.
## Cross-RID CI qualification (M3.2c)

CI run https://github.com/jenolaszlo-sketch/minigraf-sharp/actions/runs/39051498502
on revision `90120c5` (all 11 jobs green):
- open-options harness 11/11 on net8.0 and net10.0 on each `dist`
  runner (win-x64, linux-x64, osx-arm64);
- shim harness 24/24, prepared 26/26, transaction 18/18, Rust probes
  18/18 on every RID; packaging, verifier/tamper and NativeAOT
  consumers green (M3.1 regression untouched).

Evidence-bound shim artifacts recorded in `native/upstream.json`
(`qualified: true`, per-RID toolchain rustc 1.99.1 / cbindgen 0.29.4):

| RID | Shim SHA-256 | Bytes |
| --- | --- | --- |
| win-x64 | `762A8F0DC13105EA7A890A73283E0B428454781976B9BB58217F31948135DA64` | 2278400 |
| linux-x64 | `643660818CF52AFB20A36E5CA57E523E46EB2B38DB6A3F7D5E8D9BAE872E0055` | 2958896 |
| osx-arm64 | `CF45026639B99C6742E0BE7AEE5BDF62F0BA530E33291901B06C79B9CF64BCB8` | 2534960 |

The downloaded staging artifacts were re-verified locally
(`eng/Verify-NativeStaging.ps1`): all three RIDs pass with no divergence
notes. Package inventory is unchanged; `linux-arm64`/`osx-x64` remain
unqualified.
