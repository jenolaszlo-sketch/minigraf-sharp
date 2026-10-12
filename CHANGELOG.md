# Changelog

Format follows Keep a Changelog. Versions follow SemVer for the managed
package and never copy the upstream Minigraf version number.

## [Unreleased]

### Added

- M0 repository scaffolding (`net8.0;net10.0`, warnings as errors).
- Pinned upstream baseline: Minigraf core `v2.0.5`, minigraf-c `v2.0.5`.
- Archived `native/include/minigraf.h` with SHA-256 provenance.
- Deterministic native loader (`MINIGRAFSHARP_NATIVE_PATH` override, no PATH probing).
- Internal `LibraryImport` interop for `minigraf_open_in_memory` / `minigraf_close`.
- Public `MinigrafDatabase.OpenMemory()` lifecycle with `SafeHandle` ownership.
- M0 unit, integration and QuickStart verification.
- M1a raw Datalog execution: `MinigrafDatabase.ExecuteRaw(string)` returns
  native JSON as a managed string with exactly-once `minigraf_string_free`
  ownership, immediate borrowed `minigraf_last_error` capture, strict UTF-8
  handling, `MinigrafExecutionException`, and a per-database operation gate.
- M1b typed results: `MinigrafDatabase.Execute(string)` decodes raw JSON
  into an immutable `MinigrafResult` hierarchy (`MinigrafTransactionResult`,
  `MinigrafRetractionResult`, `MinigrafOkResult`, `MinigrafQueryResult` with
  `MinigrafRow`/`MinigrafValue`), failing closed with
  `MinigrafResultException` on malformed shapes.
- M1c file-backed persistence: `MinigrafDatabase.Open(string)` (UTF-8 path
  as supplied, honest `MinigrafOpenException` without a fake native
  diagnostic) and `Checkpoint()` (verified 0/−1 contract, same
  per-database gate, `MinigrafCheckpointException`), with close/reopen
  proof on `win-x64`. Recovery qualification remains deferred.
- M1d v2.0.5 qualification: reproducible recovery, corruption, and temporal
  probes in `tests/MinigrafSharp.QualificationTests` plus
  `docs/v2-qualification.md`. Reproduces and classifies the known v2.0.5
  limitations (same-transaction multi-values #371, valid-time replacement
  #435, old-graph/newer-WAL #535); confirms normal persistence, checkpoint,
  process-kill recovery, truncation fail-closed, and temporal reads. No
  public API change and no wrapper workarounds.
- M2 distribution: `MinigrafSharp` `0.1.0-preview.1` NuGet package
  (`net8.0`/`net10.0` + `runtimes/win-x64/native/minigraf.dll`, no managed
  dependencies), with `eng/Verify-Package.ps1`, `eng/Test-PackageConsumer.ps1`,
  `eng/Test-AotConsumer.ps1`, `eng/Test-TrimmedConsumer.ps1`, and an
  `samples/MinigrafSharp.AotSmoke` sample. `win-x64` is the only packaged
  and qualified RID; other RIDs stay deferred. No public API change; no
  NuGet.org publication.
