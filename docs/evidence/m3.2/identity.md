# M3.2f - Runtime identity (evidence)

Status: implemented and locally qualified on win-x64 (net8.0/net10.0);
cross-RID CI qualification appended below/after the slice commit. Basis:
ADR 0003 (accepted) identity policy; anchors M3.2e source `0eaa05b`,
evidence `2b71b50`, CI run 39088870754.

## Surface

`MinigrafSharp.MinigrafRuntimeIdentity` (new public type; surface 23 -> 24):

- `PackageVersion` - managed NuGet package version (informational version
  with semver build metadata, i.e. the embedded source revision, removed).
- `ShimAbiVersion` - shim ABI declared by the verified token (`0.1.0`).
- `ShimSourceRevision` - shim source revision (`1`).
- `CoreVersion` - pinned Minigraf core version (`2.0.7`).
- `CoreCommit` - pinned core commit
  (`2d2e5d75f28e91d61be9f293de895e2271e62a29`).
- `MinigrafRuntimeIdentity.Current` loads and identity-verifies the shim
  (the same gate every database open uses) before returning immutable
  values. A refused candidate fails with `MinigrafNativeLoadException`.

No mutable identity state is kept. Values are derived from the existing
build-composed `mgs_shim_identity` token (already verified byte-exact by
the loader) plus the assembly's informational version. No native exports
were added (ABI remains 22), no header change, and no Minigraf core
version API was invented.

## Identity concepts kept separate

- The token is build-declared compatibility/provenance: shim ABI,
  shim source revision, core version and core commit. It is not a
  cryptographic measurement of the loaded binary.
- Exact per-RID binary hashes, toolchains and artifact provenance remain
  qualification evidence (`native/upstream.json`, slice evidence), not
  runtime API.
- Managed package identity is separate from core identity and shim
  compatibility identity; the class exposes them as distinct fields.

## Loader guarantees preserved

- `Current` calls `ShimNativeLoader.EnsureLoadedAndVerified()`; identity is
  therefore only observable through a successfully verified shim.
- Rejected candidates are not cached: the harness refuses the official
  binary under the shim import first, then obtains a valid identity and
  operates normally in the same process (recovery proven).
- The prior M3.1 refusal matrix and loader ordering are unchanged.

## Token validation (internal, test-visible)

`ParseAndValidate` consumes the closed token (exactly `shim_abi`,
`shim_source_revision`, `core_version`, `core_commit`) and requires exact
agreement with the pinned constants. Rejected: malformed JSON,
non-object roots, truncated tokens, missing fields, unexpected
properties, wrong field types, empty strings, incorrect ABI, incorrect
source revision, incorrect core version and incorrect core commit. All
rejections raise `MinigrafNativeLoadException` with a diagnostic naming
the field/reason; failure never yields a partially populated identity.

## Qualification executed (local win-x64)

- New `tests/MinigrafSharp.RuntimeIdentityQualificationTests`: 14/14 on
  net8.0 and net10.0 (refusal-then-recovery first in the process,
  provenance match against `native/upstream.json` + `identity.json` and
  the package version, stability across calls, closed-token structure,
  malformed/missing/wrong-type/empty/ABI/revision/core-version/
  core-commit/extra-property negatives, provenance-not-hash check).
- Full regression on both TFMs: unit (now 24-type public-surface
  closure, including `MinigrafRuntimeIdentity`), integration,
  qualification, shim 26/26, prepared 26/26, transactions 18/18,
  options 11/11, structured errors 19/19, typed values 15/15; Rust 14
  unit tests + 18 probes; solution builds 0 warnings; format clean.
- No native source change in this slice: the shim serializer/ABI and
  header pin (`38B7FEE0...BA92`) are unchanged; packaging/tamper and
  NativeAOT consumers are re-run by the pipeline.

## Independent review checklist (performed)

- No new ABI export (22), no header change, no new identity mechanism.
- Failed identity check prevents use before any handle is returned
  (`EnsureLoadedAndVerified` runs first).
- Rejected candidates do not poison later valid resolution (proven).
- No mutable identity state; repeated reads are stable and immutable.
- No binary hash or measurement is exposed as runtime identity; provenance
  remains evidence-only.
- Package/core/shim identity concepts remain distinct.
- Public surface grows by exactly the approved type; closure test
  updated (24).
- No loader, transaction, option, error or value semantics changed.

## Limitations

- `PackageVersion` reports the managed package version without build
  metadata; the embedded source revision is intentionally not part of the
  package version.
- Core identity is the pinned build-declared value; there is no upstream
  runtime version API (unchanged limitation).
- Per-RID CI results and evidence-bound shim hashes are appended after
  the slice run; `linux-arm64`/`osx-x64` remain unqualified. No
  publication.
## Cross-RID CI qualification (M3.2f)

CI run https://github.com/jenolaszlo-sketch/minigraf-sharp/actions/runs/39091125054
on revision `e92f17d` (all 11 jobs green):
- runtime-identity harness 14/14 on net8.0 and net10.0 on each `dist`
  runner (win-x64, linux-x64, osx-arm64);
- Rust 14 unit tests + 18 probes, shim 26/26, prepared 26/26,
  transactions 18/18, options 11/11, structured errors 19/19, typed
  values 15/15 on every RID; packaging, verifier/tamper and NativeAOT
  consumers green.

No native source change was made in this slice, so the shim ABI and
header pin are unchanged (`38B7FEE0...BA92`). Evidence-bound shim
artifacts recorded in `native/upstream.json` (`qualified: true`, rustc
1.99.1 / cbindgen 0.29.4 per RID):

| RID | Shim SHA-256 | Bytes |
| --- | --- | --- |
| win-x64 | `F776AC6F2AFA7B65906C6A0ABE87EAE9CB323487767D528FCFB7BD7218B9CB08` | 2281472 |
| linux-x64 | `36DF913A482FAD16D45C3F4564C0D1C7C4182CCDFCC90F9CAFA67DAD0DD13AB8` | 2960536 |
| osx-arm64 | `45B3654C2859A6ACB71A373A48CC29997D868F47231BFC2D929747C9A8451759` | 2535024 |

Reproducibility observation (matches the recorded convention):
linux-x64 and osx-arm64 rebuilt byte-identically to the M3.2e
qualification; win-x64 differs only because of PE linker timestamps (no
byte-identical claim is made on Windows). Downloaded staging artifacts
were re-verified locally (`eng/Verify-NativeStaging.ps1`): all three
RIDs pass. ABI: 22 exports; public surface: 24 types.
`linux-arm64`/`osx-x64` remain unqualified. No publication occurred.
