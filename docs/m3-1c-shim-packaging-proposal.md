# M3.1c checkpoint proposal - shim packaging and multi-RID qualification

Status: approved objective statement of 2026-10-17 ("the owned Minigraf
prepared-query shim is packaged and independently qualified for win-x64,
linux-x64, and osx-arm64; managed consumption from the produced NuGet
package preserves the M3.1a/b semantics and provenance guarantees on each
qualified RID"); this document is the checkpoint record fixed before
implementation. Basis: the immutable M3.1a anchor commit `480471e` (native
defects, if any surface here, get an explicit M3.1a requalification, never
a silent fold), the M3.1b commit `42a7269`, ADR 0002, and
docs/packaging.md's "add a RID" procedure.

The user approved the execution path for the two non-Windows RIDs
(question answered 2026-10-17): CI runners qualify both (the existing
multi-RID pipeline is extended; a branch is pushed and `ci.yml` runs the
shim on `ubuntu-latest` and `macos-latest`).

## Scope

Deliver the owned shim inside the existing `MinigrafSharp` package for the
already-qualified RID set, and extend every qualification gate so each
packaged RID's shim is qualified by execution on its own OS:

- win-x64: full local execution (build, harnesses, package, clean
  consumer) on this development host, as before.
- linux-x64 and osx-arm64: built on their own CI runners (no
  cross-compilation from Windows), with the shim harness, the managed
  prepared-query qualification harness, and the package consumption gate
  executed there.

The package RID set does not change (no linux-arm64, no osx-x64). The
managed API does not change. The M3.1a/b semantics and provenance
guarantees carry over per-RID unchanged: identity verification before any
handle is returned, byte-exact pinned token, failed loads never cached,
cascade ownership, and no substitution between libraries.

## Design

### Provenance and staging

- `native/upstream.json` gains a `shim.assets` entry per packaged RID
  (`binary`, `binary_sha256`, `binary_bytes`, `qualified`). A shim asset's
  `qualified` flag flips only in an evidence commit after the executed
  green run on that RID is recorded; build scripts never touch it.
- `eng/Build-Shim.ps1` gains `-Rid` (default host RID, validated against
  the host OS/architecture: refuse to stage a RID that does not match the
  host; no cross builds this slice). It stages
  `native/staging/<rid>/minigrafsharp_shim.<dll|so|dylib>` plus a per-RID
  `minigrafsharp-shim.json` manifest (hash, bytes, identity token inputs,
  toolchain versions, core commit), then refreshes the matching
  `shim.assets` hashes in `native/upstream.json`.
- The identity token is RID-independent (`identity.json` + upstream
  `minigraf` block only), so the M3.1b pinned managed token is valid on
  every qualified RID.

### Package layout and pack gate

- `src/MinigrafSharp/MinigrafSharp.csproj` adds shim `<None>` items for
  `win-x64`/`linux-x64`/`osx-arm64` at `runtimes/<rid>/native/`
  (`minigrafsharp_shim.dll`, `libminigrafsharp_shim.so`,
  `libminigrafsharp_shim.dylib`) and extends the pack-time requirement so
  packing fails unless all six native assets are staged.
- `eng/Verify-NativeStaging.ps1` verifies the full staged inventory per
  RID (official binary + official manifest + shim binary + shim
  manifest), both hashes against the pins, both manifests' fields, and
  both binaries' container format/architecture.
- `eng/Verify-Package.ps1` verifies both per-RID native entries and both
  SHA-256 pins inside the `.nupkg`; the forbidden-payload patterns already
  accept binaries only under `runtimes/<rid>/native/` and `lib/<tfm>/`.

### Harnesses become RID-aware

- `tests/MinigrafSharp.ShimTests` and
  `tests/MinigrafSharp.PreparedQualificationTests` deploy the *host RID's*
  staged assets (via `$(NETCoreSdkRuntimeIdentifier)` instead of the
  hard-coded `win-x64`), so the same harness runs on any qualified runner.
- `eng/Test-Dist.ps1` gains, after the existing suites: shim build+stage
  (`eng/Build-Shim.ps1 -Rid`), staging verification, and both shim-related
  harnesses for net8.0 and net10.0. The `qualified` flag stays untouched;
  CI green run URLs become the evidence used to flip it.
- `.github/workflows/ci.yml` `dist` job installs the stable Rust toolchain
  and cbindgen before running `Test-Dist.ps1`; the `pack` and `consume`
  jobs consume the now-complete staging artifacts without structural
  changes.

### Package consumption (M3.1a/b semantics through NuGet)

- `eng/Test-PackageConsumer.ps1`'s clean consumer program adds a prepared
  query exercise (prepare, bind, execute, assert values) so the produced
  package proves shim load + identity on each qualified RID; per-RID
  markers assert it.
- The deployed shim asset is hash-verified against its pinned metadata at
  the consumer output, and the `MINIGRAFSHARP_SHIM_NATIVE_PATH` override
  matrix (missing / corrupt / wrong-architecture → fail-closed
  `MinigrafNativeLoadException`) runs beside the existing official-native
  matrix.

## Qualification matrix (fixed)

- win-x64, locally: full suite list (existing unit/integration/
  qualification suites, shim harness 14 cases, managed prepared harness
  26 cases, package verify, tamper matrix, clean consumer × TFMs).
- linux-x64 and osx-arm64, on their own runners: cargo `--locked` unit
  tests, shim harness + prepared harness (net8.0, net10.0), managed
  real-engine suites (unchanged), package consumption with the prepared
  exercise and shim override matrix.
- Evidence: run URLs, job names, artifact hashes, staged binary hashes —
  appended to `docs/verification.md`; shim `qualified` flags flipped only
  by that evidence commit.

## Explicit non-goals this slice

- No managed API change, no new exception types, no loader behavior
  changes (the package deployment path is exercised, not rewritten).
- No new RIDs; no cross-compilation; no publishing dispatch
  (`publish.yml` remains explicit-only).
- No changes to Gagamba/Penghou/sibling repositories; `ROADMAP.md`
  untouched.

## Pending-by-design items recorded in evidence

- Runner-side results for linux-x64/osx-arm64 exist only if the pushed
  workflow actually ran green; anything not executed stays pending,
  never passed.
- The `dotnet <dll>` harness invocation caveat from M3.1b evidence
  applies to runner execution (apphost invocation).
