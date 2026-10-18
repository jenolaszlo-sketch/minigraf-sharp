# Release review - 0.1.0-preview.5 (M3.2 native surface)

Status: review performed 2026-10-18 before the release commit. Decision:
release the qualified M3.2 surface as `0.1.0-preview.5` only through the
trusted-publishing pipeline, from the exact artifact produced by CI for
this revision. The release revision is metadata/documentation only.

## Reviewed inputs

- ADR 0003 (`docs/decisions/0003-rich-native-surface.md`), accepted and
  closed at M3.2g.
- Evidence `docs/evidence/m3.2/`: per-slice records plus
  `qualification.md` (integrated scenarios and invariants) and `review.md`
  (ADR 0003 closure, independent release review, release-readiness
  verdict).
- M3.2g qualified source `33bda67`, evidence `1bea537`, CI run
  https://github.com/jenolaszlo-sketch/minigraf-sharp/actions/runs/39093197042
  (11/11 jobs green; integrated 13/13 and the full matrix on win-x64,
  linux-x64 and osx-arm64, net8.0/net10.0).
- Source, scripts and workflows at this release-prep revision.

## Freeze declaration

- Native ABI: **22 exports**, frozen (generated header authoritative;
  pin `38B7FEE0FD4A1454D63E12E0CEEBCD22B756316E342E719D88E95C20570FBA92`,
  6745 bytes).
- Public managed surface: **24 types**, frozen for the preview line,
  enforced by the public-surface closure test in `tests/MinigrafSharp.Tests`
  (both TFMs). The M3.2 additions are `MinigrafOpenOptions`,
  `MinigrafSyncMode`, `MinigrafNativeErrorCategory`,
  `MinigrafWriteTransaction`, `MinigrafRuntimeIdentity`, plus the
  append-only `MinigrafValueKind.Ref`/`Keyword` values; the M3.1 surface is
  unchanged and `MinigrafDatabase` is untouched.
- Bounded claims preserved: rules are not transactional; prepared queries
  executed inside an open transaction see committed facts only;
  `Synchronous.Normal` makes no crash-durability claim; per-RID binaries
  are evidence, not runtime identity.

## Independent checks performed

1. **Surface vs ADR 0003.** The closure test passes on net8.0 and net10.0
   (24 types); no Interop/Native type is exported and no public member
   exposes `nint`/`IntPtr`/`SafeHandle`.
2. **ABI.** The generated header at this revision is byte-identical to the
   M3.2f/M3.2g pin (no native change in the release revision); the shim
   harness and `Verify-Package.ps1` chain-of-custody checks pass.
3. **Difference from the M3.2g qualified baseline.** `git diff 33bda67`
   over the release revision is limited to the package version, the
   CHANGELOG release section, README status text, `docs/compatibility.md`,
   `docs/packaging.md`, the `eng/Test-PackageConsumer.ps1` usage example,
   the `verify-published.yml` release notes and this review. No managed or
   native behavior, ABI, public type, or semantics change.
4. **Version-dependent identity.** `MinigrafRuntimeIdentity.PackageVersion`
   is asserted (identity harness) to be the preview line and to equal the
   assembly informational version without build metadata; the bump to
   `0.1.0-preview.5` keeps that invariant.
5. **Provenance.** Per-RID engine/shim hashes and toolchains are recorded
   in `native/upstream.json` (`qualified: true`) and re-verified by
   `eng/Verify-NativeStaging.ps1`; `linux-arm64`/`osx-x64` remain
   unqualified.
6. **Release path.** `publish.yml` resolves and validates a green 11-job
   `ci.yml` push run for its own commit, downloads that run's immutable
   `nupkg` and staging artifacts, verifies the package (including the shim
   chain of custody) and publishes via GitHub OIDC trusted publishing;
   it never rebuilds. `verify-published.yml` re-validates the same run,
   waits for public indexing, runs the six fresh-feed consumers, then
   creates the annotated tag and GitHub pre-release (the `GH_REPO` fix
   from preview.4 is in place).
7. **Repository state.** Only the user-owned `ROADMAP.md` edit is
   uncommitted; it is excluded from this release.

## Findings

None. No semantic, ABI or public-surface change is part of this release
revision. The only changes are the package version and release
documentation, as required for a versioned, qualified artifact.

## Release procedure (exact artifact, no rebuild)

1. Release commit: this review + the version bump to `0.1.0-preview.5` +
   the release documentation updates.
2. Push to `main`; require the full 11-job `ci.yml` run for that exact
   commit to be green.
3. Dispatch `publish.yml` at that commit: it validates the run, downloads
   that run's `MinigrafSharp.0.1.0-preview.5.nupkg`, verifies it
   (including the shim chain of custody), and publishes it.
4. `verify-published.yml` runs chained: public-feed consumption on
   Windows/Linux/macOS x net8/net10 (NativeAOT coverage included), then
   tag `v0.1.0-preview.5` and the GitHub pre-release.
5. Post-release record appended to `docs/verification.md` (release SHA,
   CI/publish/verify run identities, package SHA-256, tag).

## Exclusions

- `linux-arm64` and `osx-x64` remain unbuilt and unqualified (never
  recorded as passed).
- `0.1.0-preview.3` and `0.1.0-preview.4` are already published and
  immutable; this is a new version produced from qualified source, not a
  republish.
- No API/ABI semantics change; no packaging-layout change beyond the M3.2
  surface already qualified.
