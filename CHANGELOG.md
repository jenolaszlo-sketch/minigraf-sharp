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
  dependencies), with `eng/Verify-Package.ps1` and a clean package consumer.
  `win-x64` is the only packaged and qualified RID; other RIDs stay deferred.
  No public API change; no NuGet.org publication.
- M2.5 hardening (`docs/hardening.md`): malformed/hostile result corpus,
  bounded fuzz, strict UTF-8 negatives, execute/lifecycle/persistence stress,
  independent- and same-handle concurrency, dispose race, native loader
  negative matrix, PATH/cwd contamination, override matrix, package tamper
  self-test, privacy and legal inventory review. Fixed one fail-closed defect:
  the result decoder now translates `JsonElement.GetString()` reader failures
  (e.g. an unpaired surrogate escape) into `MinigrafResultException` instead
  of leaking `InvalidOperationException`. That content change invalidated
  `0.1.0-preview.1`; the corrected release candidate is `0.1.0-preview.2`,
  requalified end to end. No public API change; no NuGet.org publication.
- Release infrastructure (`0.1.0-preview.3`): multi-RID CI qualification
  (`win-x64`, `linux-x64`, `osx-arm64`) that stages and verifies each pinned
  native asset on its own OS and runs the real-engine suites; the pack job
  assembles the three-RID package from those exact staging artifacts; clean
  package consumers run for 3 OS x net8/net10 with NativeAOT on net10.0; and
  a trusted-publishing `publish.yml` (GitHub OIDC via `NuGet/login@v1`,
  repository secret `NUGET_USER`, no long-lived API key) plus
  `verify-published.yml`. AOT/trimming moved into
  `eng/Test-PackageConsumer.ps1`; `samples/MinigrafSharp.AotSmoke`,
  `eng/Test-AotConsumer.ps1` and `eng/Test-TrimmedConsumer.ps1` were removed.
  No public API change; publication is a separate explicit dispatch.
- Maintenance checkpoint: pinned upstream baseline refreshed from Minigraf
  core/minigraf-c `v2.0.5` to `v2.0.7` (no C ABI change; the additional
  `checkpoint`/`last_error` surface is unchanged). Every 2.0.7 asset and the
  header are hash-recorded in `native/upstream.json`; `win-x64` reran the
  full qualification locally (positive controls for upstream #608/#610 now
  observed fixed; #371/#287, #477, #435, #374, #535 confirmed still open on
  the 2.x line; #554/#555/#558 remain not reachable through the wrapper's
  serialized per-database surface; #603 recorded core-side, managed recheck
  deferred to the M3.1 prepared-query work). Observable consumer change:
  queries hit by #608/#610 now return the upstream-correct rows instead of
  empty or too-few rows. No public API change; managed package version
  unchanged. Identified RIDs other than `win-x64` remain not requalified and
  are never recorded as passed.
