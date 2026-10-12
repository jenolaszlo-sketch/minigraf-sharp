# Compatibility (M1c)

| Layer | Pinned |
| --- | --- |
| Minigraf core | `v2.0.5` (`a861e55`) |
| minigraf-c | `v2.0.5` (`8422f9f`) |
| C header | `native/include/minigraf.h`, SHA-256 `8603C074…ADE7D` |
| MinigrafSharp | `0.1.0-preview` line (unreleased) |
| Managed TFMs | `net8.0`, `net10.0` |
| Qualified RID (M1c) | `win-x64` (lifecycle, raw/typed execution, file persistence) |
| Upstream file format | v7 per the v2.0.5 release notes (recorded, not probed: M1c parses no file internals) |

Recorded-but-unqualified RIDs: `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64` (assets hashed in `native/upstream.json`, gates pending).

## Upgrade rules

Per `docs/product-spec.md` §30: every upstream upgrade re-pins versions and
commits, diffs `minigraf.h`, refreshes `native/upstream.json`, re-verifies
asset hashes and exports, reruns the full matrix per RID, and updates this
file, `verification.md` and `CHANGELOG.md`. Managed package versions follow
public API impact, never the upstream number.

## Known upstream limitations (inherited, not repaired)

v2.0.5 still carries the v2.x issues listed in `docs/product-spec.md` §3.1
(same-transaction multi-values, retract/re-assert loss, valid-time windows,
non-atomic checkpoint, WAL-generation replay). M1 documents and regression-tests
them; the wrapper claims no storage semantics beyond the pinned engine.
