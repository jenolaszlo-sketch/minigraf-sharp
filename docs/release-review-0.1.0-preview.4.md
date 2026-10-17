# Release review - 0.1.0-preview.4 (M3.1 surface freeze)

Status: review performed 2026-10-17 before the release commit. Decision:
freeze the M3.1 provisional surface for the `0.1.0-preview.4` line and
release it only through the trusted-publishing pipeline, from the exact
artifact produced by CI for this revision.

## Reviewed inputs

- ADR 0002 (`docs/decisions/0002-prepared-queries-owned-shim.md`), as the
  approved basis of M3.1.
- Recorded evidence in `docs/verification.md`: "M3.1a evidence",
  "M3.1b evidence", "M3.1c evidence".
- Qualified pipeline revision `6b96043` (CI run
  https://github.com/jenolaszlo-sketch/minigraf-sharp/actions/runs/39038565443,
  all 11 jobs green) and evidence revision `24fa4e2`.
- Source and scripts at this release-prep revision.

## Freeze declaration (public surface)

The following is the complete public surface added by M3.1 and is frozen
for this release line; changing it requires a new ADR revision. It is
enforced by the public-surface closure test in
`tests/MinigrafSharp.Tests` (exact exported-type list, both TFMs).

- `MinigrafSharp.MinigrafPreparedDatabase`: `OpenMemory()`, `Open(string)`,
  `ExecuteRaw(string)`, `Execute(string)`, `Prepare(string)`,
  `Dispose()`.
- `MinigrafSharp.MinigrafPreparedQuery`: `SlotNames`, `Execute(
  IReadOnlyDictionary<string, MinigrafBindValue>)`, `Dispose()`.
- `MinigrafSharp.MinigrafBindValue` (readonly struct): `Kind`, the seven
  factories from ADR 0002, the matching `Get*` accessors, equality
  members; `MinigrafSharp.MinigrafBindKind`.
- Existing types (including `MinigrafDatabase`) are unchanged.

## Independent checks performed

1. **Surface vs ADR 0002.** The closure test passed on net8.0 and
   net10.0; exported types are exactly the M1c surface plus the M3.1
   types above, all in namespace `MinigrafSharp`; no Interop/Native
   types are exported and no public member exposes `nint`/`IntPtr`/
   `SafeHandle` (audited by reflection in the test and by grep over
   `src/MinigrafSharp`).
2. **Hard invariants 1-7.** Mapping is recorded in the evidence sections:
   invariants 1/2 by the M3.1a native probes and the M3.1b file-backed
   handoff/replay cases; 3/4 by the cascade, double-disposal and
   use-after-dispose cases in the 26-case managed harness; 5 by the
   name-routed combined resolver (strict library-name routing, no
   cross-library substitution) plus the override matrices; 6 by the
   identity token pinned in `ShimNativeLoader` and the
   official-binary-under-shim-import refusal case; 7 by grep: no source
   parses `INT-`/`API-`/`PRS-` codes or message text for behavior (codes
   appear only in evidence text).
3. **Provenance.** Pinned shim identity inputs match
   `native/upstream.json` + `native/shim/identity.json` (shim ABI 0.1.0,
   source revision 1, core 2.0.7
   `2d2e5d75f28e91d61be9f293de895e2271e62a29`); the ABI header pin is
   `0C288ABF...3A9E8`; the three shim artifacts are recorded with their
   qualified hashes and per-RID toolchain versions; official engine pins
   are unchanged from `native/upstream.json`.
4. **Qualification evidence.** M3.1a (win-x64), M3.1b (win-x64) and
   M3.1c (all three packaged RIDs, engine + shim + package consumption)
   sections are bound to exact revisions with cleanup state; pending
   items are explicitly excluded, never claimed.
5. **Release path.** `publish.yml` resolves a green 11-job `ci.yml` push
   run for its own commit, downloads that run's immutable `nupkg`,
   verifies it, and pushes it via GitHub OIDC trusted publishing
   (`NuGet/login@v1`, repository secret `NUGET_USER`, environment
   `nuget-production`); it never rebuilds.
   `verify-published.yml` re-validates the same run, verifies the
   artifact, waits for public indexing, installs the published version
   from nuget.org on 3 OS x 2 TFMs, then creates the annotated version
   tag and a GitHub pre-release idempotently.
6. **Repository state.** Only the user-owned `ROADMAP.md` edit was
   uncommitted at review time; it is excluded from this release.

## Findings (all fixed in this release-prep commit)

- F1 (governance): ADR 0002's status header still said "proposed" even
  though it is the approved basis of M3.1. Corrected to accepted with
  its evidence bindings.
- F2 (release correctness): `publish.yml` and `verify-published.yml`
  verified the package from a bare checkout, but since M3.1c
  `Verify-Package.ps1` also verifies the packaged shim against the
  staged qualification manifests. Both workflows now download the three
  `staging-<rid>` artifacts from the validated CI run before
  verification; they still never rebuild.
- F3 (release notes accuracy): the `verify-published` release-notes
  template still said the release uses Minigraf/minigraf-c v2.0.5.
  Corrected to v2.0.7 and the owned prepared-query shim.

No open semantic findings. No native or managed behavior changes are part
of this release-prep commit.

## Release procedure (exact artifacts, no rebuild)

1. Release commit: this review + version bump to `0.1.0-preview.4` +
   pipeline/doc corrections.
2. Push to `main`; require the full 11-job `ci.yml` run for that exact
   commit to be green.
3. Dispatch `publish.yml` at that commit: it validates the run, downloads
   that run's `MinigrafSharp.0.1.0-preview.4.nupkg`, verifies it
   (including the shim chain of custody), and publishes it.
4. `verify-published.yml` runs chained: public-feed consumption on
   Windows/Linux/macOS x net8/net10, then tag `v0.1.0-preview.4` and the
   GitHub pre-release.
5. Post-release record appended to `docs/verification.md` (release SHA,
   CI/publish/verify run identities, package SHA-256, tag).

## Exclusions

- `linux-arm64` and `osx-x64` remain unbuilt and unqualified (never
  recorded as passed).
- `0.1.0-preview.3` is already published and immutable; this release is
  a new version produced from the qualified source, not a republish.
- No API semantics change; no runtime-grant or packaging-layout change
  beyond the M3.1c shim asset already qualified.
