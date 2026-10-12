# Packaging

How MinigrafSharp is turned into a NuGet package and how that package is
verified. Consumer usage lives in the [README](../README.md); reliability
boundaries live in [v2-qualification.md](v2-qualification.md).

## Package identity

| Field | Value |
| --- | --- |
| PackageId | `MinigrafSharp` |
| Version | `0.1.0-preview.3` (preview line; not a stable release) |
| TFMs | `net8.0`, `net10.0` |
| RIDs | `win-x64`, `linux-x64`, `osx-arm64` |
| License | Apache-2.0 |
| Managed runtime dependencies | none |

## Layout

```text
lib/net8.0/MinigrafSharp.dll
lib/net10.0/MinigrafSharp.dll
runtimes/win-x64/native/minigraf.dll
runtimes/linux-x64/native/libminigraf.so
runtimes/osx-arm64/native/libminigraf.dylib
LICENSE
NOTICE
README.md
```

The managed assemblies have no external package dependencies. Each native
engine ships under `runtimes/<rid>/native/`, so a normal .NET build copies the
host RID's asset into the consumer output under the same path (or, for
NativeAOT publishing, beside the executable). The loader resolves it there
without any configuration; see [native-boundary.md](native-boundary.md).

## Supported RIDs

Only RIDs qualified **by execution on their own OS** are packaged
([compatibility.md](compatibility.md) has the live table). Qualification runs
in CI: the `dist` job stages and verifies the pinned asset and runs the
real-engine suites on each runner, then uploads the staging directory; the
`pack` job downloads those exact staging artifacts and builds one package.

A RID is added by: qualifying it in the `dist`/`consume` CI matrices, adding
its `<None>` item and the pack-time requirement in
`src/MinigrafSharp/MinigrafSharp.csproj`, adding it to
`eng/Verify-NativeStaging.ps1` and `eng/Verify-Package.ps1`, and updating this
file plus `compatibility.md`.

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

The asset that qualification tests is the exact asset that is packed (same
staged file, verified by hash at both ends, and the CI `pack` job consumes the
`dist` artifacts byte-for-byte).

## Building the package

```powershell
# Stage the qualified native assets, then pack.
foreach ($rid in 'win-x64','linux-x64','osx-arm64') { eng/Get-Native.ps1 -Rid $rid }
dotnet pack src/MinigrafSharp/MinigrafSharp.csproj `
  -c Release -p:MinigrafSharpEnablePack=true -o artifacts/packages
```

Packing is opt-in (`-p:MinigrafSharpEnablePack=true`); without it the project
is not packable, so ordinary builds and tests never emit a package. The pack
target fails unless all three qualified native assets are staged, so a
partial package cannot be produced silently.

## Qualification tooling

All scripts live in `eng/` and use isolated `NUGET_PACKAGES` caches and OS
temp directories outside the repository.

| Script | Purpose |
| --- | --- |
| `Get-Native.ps1` | Fetch and hash-verify the pinned native asset per RID. |
| `Verify-NativeStaging.ps1` | Verify a staged RID: inventory, binary/manifest hashes vs the pin, and container format/architecture. |
| `Test-Dist.ps1` | One RID's on-OS gate: stage, verify staging, then run the real-engine unit/integration/qualification suites. |
| `Verify-Package.ps1` | Inspect the `.nupkg`: TFMs, RID set, native hashes vs metadata, identity, legal files, no duplicate entries, no forbidden payload. |
| `Test-PackageVerifier.ps1` | Tamper matrix: build tampered packages, assert the verifier rejects each, control passes. |
| `Test-PackageConsumer.ps1` | Clean external consumer for one TFM: install, run in-memory (typed + raw) + Unicode + file-backed; loader hardening (PATH/cwd, missing native, override matrix); `-Aot`/`-Trim` publish + run for a RID; `-UsePublicFeed` installs from nuget.org. |

The consumer clears `MINIGRAFSHARP_NATIVE_PATH`, runs from a temp directory
**outside** the repository (so the loader's development walk-up cannot mask a
deployment problem), and uses no `ProjectReference`.

## Release pipeline

`.github/workflows/ci.yml` runs the `format`, `dist` (per-RID on its own OS),
`pack`, and `consume` (6 × OS × TFM with NativeAOT on net10.0) jobs, and
uploads the built `nupkg` as an artifact. `.github/workflows/publish.yml`
selects and validates an exact successful `ci.yml` run for a release SHA,
downloads the immutable `nupkg`, verifies it, and publishes via GitHub OIDC
trusted publishing (`NuGet/login@v1`, repository secret `NUGET_USER`) — no
long-lived API key is stored, and the package is never rebuilt at publish
time. `.github/workflows/verify-published.yml` installs the published version
from nuget.org on Windows/Linux/macOS × net8/net10.

## Native loading in a package

Resolution order is unchanged from M1: `MINIGRAFSHARP_NATIVE_PATH` override,
then `<app>/runtimes/<rid>/native/<file>`, then `<app>/<file>`, then the
repository staging walk-up (development only). A package consumer needs no
override and no configuration. Failure to find or load the asset raises
`MinigrafNativeLoadException` with the RID, architecture, expected filename,
probed locations, and pinned versions — never a fallback to a system
library. There is no runtime download.

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
