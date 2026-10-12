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
