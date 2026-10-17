# Packaging

How MinigrafSharp is turned into a NuGet package and how that package is
verified. Consumer usage lives in the [README](../README.md); reliability
boundaries live in [v2-qualification.md](v2-qualification.md).

## Package identity

| Field | Value |
| --- | --- |
| PackageId | `MinigrafSharp` |
| Version | `0.1.0-preview.4` (preview line; not a stable release) |
| TFMs | `net8.0`, `net10.0` |
| RIDs | `win-x64`, `linux-x64`, `osx-arm64` |
| License | Apache-2.0 |
| Managed runtime dependencies | none |

## Layout

```text
lib/net8.0/MinigrafSharp.dll
lib/net10.0/MinigrafSharp.dll
runtimes/win-x64/native/minigraf.dll
runtimes/win-x64/native/minigrafsharp_shim.dll
runtimes/linux-x64/native/libminigraf.so
runtimes/linux-x64/native/libminigrafsharp_shim.so
runtimes/osx-arm64/native/libminigraf.dylib
runtimes/osx-arm64/native/libminigrafsharp_shim.dylib
LICENSE
NOTICE
README.md
```

The managed assemblies have no external package dependencies. Each native
engine ships under `runtimes/<rid>/native/`, and the owned prepared-query
shim ships beside it under the same `runtimes/<rid>/native/` path, so a
normal .NET build copies both host-RID assets into the consumer output (or,
for NativeAOT publishing, beside the executable). The loaders resolve them
there without any configuration; see [native-boundary.md](native-boundary.md)
and [decisions/0002-prepared-queries-owned-shim.md](decisions/0002-prepared-queries-owned-shim.md).

## Supported RIDs

Only RIDs qualified **by execution on their own OS** are packaged
([compatibility.md](compatibility.md) has the live table). Qualification runs
in CI: the `dist` job stages and verifies the pinned engine asset, runs the
real-engine suites, builds and stages the owned shim for that host (the
`cargo --locked` unit tests run as part of the build), re-verifies the full
staged inventory, then runs the shim and managed prepared-query harnesses on
that runner before uploading the staging directory; the `pack` job downloads
those exact staging artifacts and builds one package.

A RID is added by: qualifying it in the `dist`/`consume` CI matrices, adding
its engine and shim `<None>` items and the pack-time requirement in
`src/MinigrafSharp/MinigrafSharp.csproj`, adding it to
`eng/Verify-NativeStaging.ps1` and `eng/Verify-Package.ps1`, and updating this
file plus `compatibility.md`. The shim is never cross-compiled: each RID is
built on its own runner.

## Native asset provenance

Every packaged native asset is pinned in `native/upstream.json`: upstream
version and commit, release asset name and URL, release archive SHA-256,
extracted filename and SHA-256, architecture, and the archived header hash.
`eng/Get-Native.ps1 -Rid <rid>` downloads the exact pinned archive into a
separate `native/downloads/<rid>/` cache, verifies the archive hash, extracts
the library, verifies the library hash, stages only `<binary>` +
`minigrafsharp-native.json` under `native/staging/<rid>/`, and
`eng/Verify-NativeStaging.ps1` re-checks inventory, hashes, manifest fields
and the binary's container format/architecture (PE/x86-64, ELF/x86-64,
Mach-O/arm64-or-universal). Staged binaries are not committed.

The owned shim has its own provenance block (`shim` in
`native/upstream.json`): shim ABI and source revision, the pinned `minigraf`
crate (`=2.0.7` with its tarball hash), the generated header hash, and per-RID
binary hashes plus the runner-specific Rust toolchain versions.
`eng/Build-Shim.ps1 -Rid <rid>` regenerates the header (asserting it is
byte-identical to the tracked pin), runs the `cargo --locked` unit tests,
builds the release shim for the host, stages
`minigrafsharp_shim.<dll|so|dylib>` plus `minigrafsharp-shim.json`, and
refreshes the per-RID records. The `qualified` flags are flipped only by an
evidence commit after that RID's executed qualification is recorded.

Unlike the engine (an upstream release artifact, byte-identical everywhere),
the shim is built from source per OS and is not byte-reproducible across
linker timestamps and runner toolchains. Its hash therefore cannot be a
cross-build invariant: the chain of custody is verified end to end within a
qualification run — staged binary ↔ staged qualification manifest
(`Verify-NativeStaging.ps1`), staged binary ↔ package entry
(`Verify-Package.ps1`), package entry ↔ deployed file
(`Test-PackageConsumer.ps1`) — and the `shim.assets.<rid>.binary_sha256`
value recorded in `native/upstream.json` plus `docs/verification.md` pins the
exact artifact that was qualified (retrospective evidence), with the toolchain
versions that produced it.

The assets that qualification tests are the exact assets that are packed (same
staged files, verified by hash at both ends, and the CI `pack` job consumes
the `dist` artifacts byte-for-byte).

## Building the package

```powershell
# Stage the qualified native assets, then pack.
foreach ($rid in 'win-x64','linux-x64','osx-arm64') { eng/Get-Native.ps1 -Rid $rid }
# Build the shim on each host (Build-Shim refuses non-host RIDs); on the
# development host that is win-x64. Other RIDs' shim staging comes from the
# CI dist artifacts.
eng/Build-Shim.ps1 -Rid win-x64
dotnet pack src/MinigrafSharp/MinigrafSharp.csproj `
  -c Release -p:MinigrafSharpEnablePack=true -o artifacts/packages
```

Packing is opt-in (`-p:MinigrafSharpEnablePack=true`); without it the project
is not packable, so ordinary builds and tests never emit a package. The pack
target fails unless all six qualified native assets (engine + shim per RID)
are staged, so a partial package cannot be produced silently.

## Qualification tooling

All scripts live in `eng/` and use isolated `NUGET_PACKAGES` caches and OS
temp directories outside the repository.

| Script | Purpose |
| --- | --- |
| `Get-Native.ps1` | Fetch and hash-verify the pinned engine asset per RID. |
| `Build-Shim.ps1` | Build (with `cargo --locked` unit tests), hash, and stage the owned shim for the host RID; regenerate the header against its pin; refresh per-RID shim provenance. |
| `Verify-NativeStaging.ps1` | Verify a staged RID: inventory (engine + shim + manifests), binary/manifest hashes vs the pins, and container format/architecture. |
| `Test-Dist.ps1` | One RID's on-OS gate: stage + verify, real-engine suites, shim build + full staging verification, shim and managed prepared-query harnesses. |
| `Verify-Package.ps1` | Inspect the `.nupkg`: TFMs, RID set, engine and shim hashes vs metadata, identity, legal files, no duplicate entries, no forbidden payload. |
| `Test-PackageVerifier.ps1` | Tamper matrix: build tampered packages (engine and shim), assert the verifier rejects each, control passes. |
| `Test-PackageConsumer.ps1` | Clean external consumer for one TFM: install, run in-memory (typed + raw) + Unicode + prepared query + file-backed; deployed engine/shim hash checks; loader hardening (PATH/cwd, missing native, engine and shim override matrices); `-Aot`/`-Trim` publish + run for a RID; `-UsePublicFeed` installs from nuget.org. |

The consumer clears `MINIGRAFSHARP_NATIVE_PATH`, runs from a temp directory
**outside** the repository (so the loader's development walk-up cannot mask a
deployment problem), and uses no `ProjectReference`.

## Release pipeline

`.github/workflows/ci.yml` runs the `format`, `dist` (per-RID on its own OS:
engine staging + suites + shim build/verify + shim harnesses), `pack`, and
`consume` (6 × OS × TFM with NativeAOT on net10.0) jobs, and uploads the
built `nupkg` as an artifact. `.github/workflows/publish.yml`
selects and validates an exact successful `ci.yml` run for a release SHA,
downloads the immutable `nupkg`, verifies it, and publishes via GitHub OIDC
trusted publishing (`NuGet/login@v1`, repository secret `NUGET_USER`) — no
long-lived API key is stored, and the package is never rebuilt at publish
time. `.github/workflows/verify-published.yml` installs the published version
from nuget.org on Windows/Linux/macOS × net8/net10.

## Native loading in a package

Resolution order is unchanged from M1: `MINIGRAFSHARP_NATIVE_PATH` override,
then `<app>/runtimes/<rid>/native/<file>`, then `<app>/<file>`, then the
repository staging walk-up (development only). The owned shim resolves
independently through `MINIGRAFSHARP_SHIM_NATIVE_PATH` and the same
`runtimes/<rid>/native/` / base-directory / staging order, and verifies its
pinned identity token before any handle is returned. A package consumer needs
no override and no configuration. Failure to find or load either asset raises
`MinigrafNativeLoadException` with the RID, architecture, expected filename,
probed locations, and pinned versions — never a fallback to a system library.
There is no runtime download.

## Reproducibility

Managed builds are deterministic (`Deterministic=true`). The `.nupkg` is not
byte-for-byte reproducible (NuGet embeds timestamps), so reproducibility is
asserted over contents and identities: package file list, native hashes, and
metadata — which `eng/Verify-Package.ps1` checks.

## Deferred

- Publishing to NuGet.org (a separate, explicit `publish.yml` dispatch).
- Symbol package / SourceLink.
- Single-file publishing (native libraries deploy separately by design).
- RIDs beyond the three qualified ones.
