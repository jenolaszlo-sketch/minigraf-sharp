# Packaging

How MinigrafSharp is turned into a NuGet package and how that package is
verified. Consumer usage lives in the [README](../README.md); reliability
boundaries live in [v2-qualification.md](v2-qualification.md).

## Package identity

| Field | Value |
| --- | --- |
| PackageId | `MinigrafSharp` |
| Version | `0.1.0-preview.1` (preview line; not a stable release) |
| TFMs | `net8.0`, `net10.0` |
| License | Apache-2.0 |
| Managed runtime dependencies | none |

## Layout

```text
lib/net8.0/MinigrafSharp.dll
lib/net10.0/MinigrafSharp.dll
runtimes/win-x64/native/minigraf.dll
LICENSE
NOTICE
README.md
```

The managed assemblies have no external package dependencies. The native
engine ships under `runtimes/<rid>/native/`, so a normal .NET build copies
it into the consumer output under the same path (or, for NativeAOT
publishing, beside the executable). The loader resolves it there without any
configuration; see [native-boundary.md](native-boundary.md).

## Supported RIDs

Only RIDs qualified **by execution** are packaged
([compatibility.md](compatibility.md) has the live table). M2 packages
`win-x64` only. Other upstream assets are recorded in
`native/upstream.json` but are not packaged or advertised until they pass
the RID gate on a real runner.

A RID is added by: qualifying it by execution, adding its staged `<None>`
item and the pack-time requirement list in
`src/MinigrafSharp/MinigrafSharp.csproj`, adding it to
`eng/Verify-Package.ps1` expectations, and updating this file plus
`compatibility.md`.

## Native asset provenance

Every packaged native asset is pinned in `native/upstream.json`: upstream
version and commit, release asset name and URL, release archive SHA-256,
extracted filename and SHA-256, architecture, and the archived header hash.
`eng/Get-Native.ps1 -Rid <rid>` downloads the exact pinned archive,
verifies the archive hash, extracts the library, verifies the library hash,
and stages it under `native/staging/<rid>/`. Staged binaries are not
committed.

The asset that qualification tests is the exact asset that is packed
(same staged file, verified by hash at both ends).

## Building the package

```powershell
# Stage the qualified native asset(s), then pack.
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Get-Native.ps1 -Rid win-x64
dotnet pack src/MinigrafSharp/MinigrafSharp.csproj `
  -c Release -p:MinigrafSharpEnablePack=true -o artifacts/packages
```

Packing is opt-in (`-p:MinigrafSharpEnablePack=true`); without it the
project is not packable, so ordinary builds and tests never emit a package.
The pack target fails if a required qualified native asset is not staged,
so a managed-only package cannot be produced silently.

## Qualification tooling

All scripts live in `eng/` and use isolated `NUGET_PACKAGES` caches and OS
temp directories outside the repository.

| Script | Purpose |
| --- | --- |
| `Get-Native.ps1` | Fetch and hash-verify the pinned native asset per RID. |
| `Verify-Package.ps1` | Inspect the `.nupkg`: TFMs, RID set, native hashes vs metadata, identity, legal files, no forbidden payload. |
| `Test-PackageConsumer.ps1` | Clean external consumer per TFM: install, build, run in-memory + file-backed, assert override absent, and assert fail-closed when the native asset is missing. |
| `Test-AotConsumer.ps1` | NativeAOT publish + execute of `samples/MinigrafSharp.AotSmoke` outside the repo. |
| `Test-TrimmedConsumer.ps1` | Trimmed publish + execute. |

The consumer scripts clear `MINIGRAFSHARP_NATIVE_PATH`, run from a temp
directory **outside** the repository (so the loader's development walk-up
cannot mask a deployment problem), and use no `ProjectReference`.

## Native loading in a package

Resolution order is unchanged from M1: `MINIGRAFSHARP_NATIVE_PATH` override,
then `<app>/runtimes/<rid>/native/<file>`, then `<app>/<file>`, then the
repository staging walk-up (development only). A package consumer needs no
override and no configuration. Failure to find or load the asset raises
`MinigrafNativeLoadException` with the RID, architecture, expected filename,
probed locations, and pinned versions — never a fallback to a system
library. There is no runtime download.

## Reproducibility

Managed builds are deterministic (`Deterministic=true`). `MinigrafSharp`
and `MinigrafSharp.AotSmoke` are not byte-for-byte reproducible `.nupkg`
files (NuGet embeds timestamps), so reproducibility is asserted over
contents and identities: package file list, native hashes, and metadata —
which `eng/Verify-Package.ps1` checks.

## Deferred

- Publishing to NuGet.org (a separate, explicit action; M2 stops at a
  release-ready artifact).
- Additional RIDs (need execution on those platforms).
- Symbol package / SourceLink.
- Single-file publishing (native libraries deploy separately by design).
