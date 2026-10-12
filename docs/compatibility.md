# Compatibility (M2)

| Layer | Pinned |
| --- | --- |
| Minigraf core | `v2.0.5` (`a861e55`) |
| minigraf-c | `v2.0.5` (`8422f9f`) |
| C header | `native/include/minigraf.h`, SHA-256 `8603C074…ADE7D` |
| MinigrafSharp | `0.1.0-preview.1` |
| Managed TFMs | `net8.0`, `net10.0` |
| Packaged + qualified RID | `win-x64` (lifecycle, raw/typed execution, file persistence, storage/recovery/temporal qualification) |
| Upstream file format | v7 (`src/storage/mod.rs`: `FORMAT_VERSION = 7`) |

## RID status

Three states are distinguished: **qualified** (executed on that platform),
**packaged** (shipped in the NuGet package), and **unsupported** (neither).
M2 packages only what it qualified, so qualified = packaged.

| RID | Asset source | Packaged | Qualified | NativeAOT | Notes |
| --- | --- | --- | --- | --- | --- |
| `win-x64` | upstream windows x86_64 zip | yes | yes | yes (net10.0) | M2 baseline |
| `linux-x64` | upstream linux x86_64 | no | no | unqualified | deferred: no execution environment |
| `linux-arm64` | upstream linux aarch64 | no | no | unqualified | deferred: no execution environment |
| `osx-arm64` | upstream macos universal2 | no | no | unqualified | deferred: no execution environment |
| `osx-x64` | upstream macos universal2 | no | no | unqualified | deferred: no execution environment |

The non-`win-x64` assets are hash-recorded in `native/upstream.json` and
fetchable with `eng/Get-Native.ps1`, but are not packaged or advertised
until they pass the RID gate (native acquisition, export + architecture
verification, build/tests, clean package consumer, and NativeAOT where
claimed) on a real runner. See [packaging.md](packaging.md).

## Upgrade rules

Per `docs/product-spec.md` §30: every upstream upgrade re-pins versions and
commits, diffs `minigraf.h`, refreshes `native/upstream.json`, re-verifies
asset hashes and exports, reruns the full matrix per RID, and updates this
file, `verification.md` and `CHANGELOG.md`. Managed package versions follow
public API impact, never the upstream number.

## Minigraf v2.0.5 qualification

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
