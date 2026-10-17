# Changelog

Format follows Keep a Changelog. Versions follow SemVer for the managed
package and never copy the upstream Minigraf version number.

## [0.1.0-preview.4] - 2026-10-17

Freeze and first release of the qualified M3.1 surface. This release
contains the entries listed under Unreleased through M3.1c (typed
`MinigrafDatabase` surface, packaging and CI qualification for
`win-x64`/`linux-x64`/`osx-arm64`, the owned prepared-query shim, and the
provisional `MinigrafPreparedDatabase`/`MinigrafPreparedQuery`/
`MinigrafBindValue` surface with its 26-case managed qualification and
loader identity verification). The public M3.1 surface is frozen for this
line; see `docs/release-review-0.1.0-preview.4.md` for the independent
release review and the exact trusted-publishing procedure (artifact from
the validated CI run, never rebuilt). `linux-arm64`/`osx-x64` remain
unqualified.

- M3.2a native characterization: 16 measured probes against the pinned
  Minigraf 2.0.7 core (
ative/shim/tests/characterization.rs, run inside
  every dist job via cargo test --locked) covering transaction
  creation/commit/rollback/drop/reentrancy/blocking/failure state, open
  options (wal_checkpoint_threshold, limits, in-memory no-ops), stable
  error categories/codes, and typed Ref/Keyword identity. Evidence in
  docs/evidence/m3.2/characterization.md; design proposal (not yet
  accepted, no public types) in docs/decisions/0003-rich-native-surface.md.

### Fixed

- Release pipeline: `publish.yml` and `verify-published.yml` now download
  the per-RID staging artifacts from the validated CI run before package
  verification, since package verification includes the owned shim's
  chain of custody (staged binary -> qualification manifest -> package
  entry). Release notes now state the actual pinned upstream version.

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
- M3.1a native shim: owned prepared-query shim `native/shim/`
  (Cargo `minigrafsharp-shim 0.1.0`, pin `minigraf =2.0.7` with committed
  checksums, no upstream file copied) exposing 12 `mgs_` exports with a
  build-composed identity token (`mgs_shim_identity`; shim ABI 0.1.0 +
  core 2.0.7), the official result-JSON shapes, an official-binary
  rejection gate at the identity level, and `docs/verification.md`
  "M3.1a evidence" on `win-x64` (14/14 both TFMs). `eng/Build-Shim.ps1`
  and `tests/MinigrafSharp.ShimTests` (net8/net10) added. No managed API
  change, no loader/public-surface change, no package change; other RIDs
  not built and remain unqualified.
- M3.1b managed prepared-query surface (provisional, ADR 0002): new
  `MinigrafPreparedDatabase`/`MinigrafPreparedQuery`/`MinigrafBindValue`
  over the owned shim, with deterministic cascade ownership, a
  per-database gate, loader identity verification
  (`MINIGRAFSHARP_SHIM_NATIVE_PATH`, `mgs_shim_identity` checked
  byte-exact against the pinned provenance, official-binary refusal), and
  result decoding through the existing `MinigrafResult` contract.
  `win-x64`/net8+net10 qualification in
  `tests/MinigrafSharp.PreparedQualificationTests` (26 cases) with
  `docs/verification.md` "M3.1b evidence". Also fixed the UTF-8 BOM that
  `eng/Build-Shim.ps1` had left in `native/upstream.json` (marginal to
  `480471e`, caught by the unit harness). Public API is additive;
  `MinigrafDatabase` unchanged; shim package distribution deferred
  (pending, never passed); other RIDs unbuilt and unqualified.
- M3.1c shim packaging and multi-RID qualification: the owned
  prepared-query shim now ships beside the engine in the NuGet package
  for every packaged RID (`win-x64`, `linux-x64`, `osx-arm64`), and the
  full qualification pipeline runs on each RID's own OS: native
  `cargo --locked` tests, shim harness (14/14), managed prepared-query
  harness (26/26), engine suites, staging verification, package assembly,
  package verifier self-test, and package consumption with a prepared
  query plus the `MINIGRAFSHARP_SHIM_NATIVE_PATH` fail-closed matrix.
  CI run 39038565443 on revision `6b96043` is green across format, three
  `dist` jobs, `pack`, and six `consume` jobs (net8/net10, NativeAOT on
  net10). Evidence in `docs/verification.md` "M3.1c evidence", including
  the qualified artifact hashes and per-RID toolchain records. Fixed
  along the way: POSIX-only shim resolver registration, a Mach-O magic
  comparison bug, CRLF header canonicalization, and a consumer probe
  path/cleanup bug. No public API change since M3.1b; package RID set
  unchanged; `linux-arm64`/`osx-x64` remain unbuilt and unqualified.
