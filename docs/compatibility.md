# Compatibility

| Layer | Pinned |
| --- | --- |
| Minigraf core | `v2.0.7` (`2d2e5d7`) |
| minigraf-c | `v2.0.7` (`4b598c0`) |
| C header | `native/include/minigraf.h`, SHA-256 `8603C074…ADE7D` |
| MinigrafSharp | `0.1.0-preview.5` (qualified release candidate) |
| Managed TFMs | `net8.0`, `net10.0` |
| Packaged + qualified RIDs | `win-x64`, `linux-x64`, `osx-arm64` |
| Upstream file format | v7 (`src/storage/mod.rs`: `FORMAT_VERSION = 7`) |

## RID status

Three states are distinguished: **qualified** (executed on that platform by
the `ci.yml` `dist`/`consume` jobs), **packaged** (shipped in the NuGet
package), and **unsupported** (neither). The package ships exactly the RIDs
that CI qualified on their own OS, so qualified = packaged.

| RID | Asset source | Packaged | Qualified | NativeAOT | Notes |
| --- | --- | --- | --- | --- | --- |
| `win-x64` | upstream windows x86_64 zip | yes | yes (v2.0.7 + M3.2: CI run 39093197042, 2026-10-18) | yes (net10.0) | lifecycle + execution + persistence + v2 qualification + M3 prepared/transaction/options/error/value/identity + shim packaging |
| `linux-x64` | upstream linux x86_64 | yes | yes (v2.0.7 + M3.2: CI run 39093197042, 2026-10-18) | yes (net10.0) | CI-qualified on `ubuntu-latest`; engine suites + shim harnesses + package consumption |
| `osx-arm64` | upstream macos universal2 | yes | yes (v2.0.7 + M3.2: CI run 39093197042, 2026-10-18) | yes (net10.0) | CI-qualified on `macos-latest`; engine suites + shim harnesses + package consumption |
| `linux-arm64` | upstream linux aarch64 | no | no | unqualified | deferred: no runner yet |
| `osx-x64` | upstream macos universal2 | no | no | unqualified | deferred: universal2 asset matches, no runner yet |

Every packaged asset is hash-recorded in `native/upstream.json` (engine
release bytes; owned shim artifacts with per-RID toolchain versions),
staged and re-verified by `eng/Verify-NativeStaging.ps1`, and re-checked
inside the package by `eng/Verify-Package.ps1`. The owned prepared-query
shim ships beside the engine for every packaged RID; its per-run
qualification (native `cargo --locked` tests, shim harness, managed
prepared harness, package consumption) is executed on each RID's own OS.
Per-run results are in [verification.md](verification.md). See
[packaging.md](packaging.md).

## Upgrade rules

Per `docs/product-spec.md` §30: every upstream upgrade re-pins versions and
commits, diffs `minigraf.h`, refreshes `native/upstream.json`, re-verifies
asset hashes and exports, reruns the full matrix per RID, and updates this
file, `verification.md` and `CHANGELOG.md`. Managed package versions follow
public API impact, never the upstream number.

## Minigraf v2.0.5 qualification (historical M1d evidence)

Measured on the v2.0.5 binaries; the facts below were recorded at the
v2.0.5 pin and are kept verbatim. The v2.0.7 requalification status of each
item is in the next section and in [v2-qualification.md](v2-qualification.md).

Full evidence and per-probe results: [v2-qualification.md](v2-qualification.md).

### Safe / qualified

- Normal file-backed operation, returned committed transactions, normal
  close/reopen, explicit `Checkpoint()`.
- Clean dispose flushes the WAL (close-time checkpoint).
- Committed writes recover after abrupt process termination (process kill,
  not power loss).
- `:valid-at` window reads and `:as-of` transaction-time reads not
  intersecting the issues below.
- Point lookups (`[?e :attr literal]`) and scans.

### Known limitations (reproduced on v2.0.5)

- Same-transaction multi-valued facts read back as one value until a
  checkpoint/reopen index rebuild (#371).
- A later valid-time assertion does not replace an earlier window (#435).
- Restoring an older `.graph` beside a newer `.wal` is accepted and the
  newer WAL replays (#535).
- Truncated files fail closed; a torn WAL loses uncheckpointed entries.

### Avoid on v2.0.5 (advice, not enforced by the wrapper)

- Multiple same-transaction values for one attribute when all must be
  immediately queryable.
- Depending on valid-time window replacement.
- Restoring a `.graph` while a newer `.wal` is present.
- Assuming recursive rules survive reopen (re-declare per open).

## Minigraf v2.0.7 requalification (2026-10-17, win-x64)

The pin moved from v2.0.5 to v2.0.7 with no C ABI change (the v2.0.6 and
v2.0.7 `minigraf.h` are hash-identical to the archived v2.0.5 header).
Observed on the v2.0.7 binaries; exact checks in
[v2-qualification.md](v2-qualification.md):

- Observed fixed by the pin bump (upstream release-note fixes, reproduced
  here as positive controls): #608 (rule calls with literal arguments) and
  #610 (`or` without shared variables now joins as a cross product). Caller
  behavior change: queries that previously returned empty or missing rows
  now return the upstream-correct rows; stored data was never affected.
- Not reachable through the wrapper's serialized per-database surface, so
  no wrapper-observable change up to 2.0.7: #554/#555/#558 (upstream fixed
  them engine-side in v2.0.6; the wrapper cannot interleave execute and
  checkpoint on one handle, so the wrapper surface never exhibited them).
- Not reachable through the official C surface: #603 (prepared queries
  dropped a literal `:as-of`/`:valid-at` when binding slots; fixed in
  v2.0.7 core, but no prepared-statement function exists in minigraf-c).
  Managed recheck scheduled with the M3.1 prepared-query shim.
- Still open on the 2.x line (unchanged from v2.0.5): same-transaction
  multi-valued facts (#371/#287), WriteTransaction retract/re-assert (#477),
  valid-time window replacement (#435), overlapping window non-replacement
  (#374), old-graph/newer-WAL acceptance (#535). The "avoid" guidance above
  carries forward unchanged on v2.0.7.
- Qualified RID for this requalification: `win-x64` locally; all three
  packaged RIDs requalified on v2.0.7 by CI run 39038565443 (2026-10-17,
  `dist` + `pack` + six `consume` jobs green, including the owned shim's
  native/managed harnesses and package consumption). No unqualified RID
  is recorded as passed.
