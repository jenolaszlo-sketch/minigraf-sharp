# Hardening (M2.5)

M2.5 attacked the M2 preview from the edges. It added **no public API** and
found one real fail-closed defect in the result decoder, which was fixed and
forced a package version bump (`0.1.0-preview.1` → `0.1.0-preview.2`).
Detailed command output lives in [verification.md](verification.md).

Baseline compared against: `d56e021`.

> Note: this record describes the M2.5 hardening of the `preview.2` candidate.
> The AOT/trimming helpers it names (`eng/Test-AotConsumer.ps1`,
> `eng/Test-TrimmedConsumer.ps1`, `samples/MinigrafSharp.AotSmoke`) were
> folded into `eng/Test-PackageConsumer.ps1` (`-Aot`/`-Trim`) by the preview.3
> release infrastructure; see [verification.md](verification.md).

## Result-decoder defect (fixed)

`MinigrafResultDecoder` documents a closed contract: exactly the shapes
emitted by minigraf-c v2.0.5 decode, and **everything else fails with
`MinigrafResultException`**. A hostile result string containing an unpaired
UTF-16 surrogate escape (`{"variables":["?v"],"results":[["\ud800"]]}`)
parsed successfully but made `JsonElement.GetString()` throw
`InvalidOperationException` — a non-Minigraf exception escaping the result
boundary, so `catch (MinigrafException)` would miss it.

Fix: string reads now go through a helper that translates any reader failure
into `MinigrafResultException`. Behavior for all valid and previously-handled
inputs is unchanged. This is a defensive fail-closed fix: the input cannot be
produced by the pinned upstream binding (Rust/`serde_json` emit only valid
UTF-8, which cannot encode lone surrogates), but the result boundary must stay
closed regardless of the caller's input.

Consequence: `0.1.0-preview.1` was **not** shipped; the corrected candidate is
`0.1.0-preview.2`, requalified end to end.

## Hardening coverage

| Area | Workload | Result |
| --- | --- | --- |
| Malformed result corpus | 34 hand-built hostile JSON inputs | 34/34 `MinigrafResultException` |
| Edge/malformed inputs | 10 surrogate/duplicate/oversize/Unicode inputs | terminate predictably, 0 unexpected |
| Bounded fuzz | 500 deterministic seeded inputs | 0 unexpected exception types |
| Large results | 1×5000 rows, 5×500 rows, 250k-char string | decode with exact indexing |
| Strict UTF-8 helper | ascii/multibyte/emoji/NUL/null/empty + 4 invalid sequences | invalid inputs throw, no lossy decode |
| Failure/success alternation | 300 invalid + 300 valid on one handle | 300 diagnosed, 300 succeed |
| Execute soak | 10,000 mixed transact/query/raw/invalid | 8000 ok / 2000 diagnosed |
| Lifecycle | 5,000 in-memory cycles | complete, handle delta bounded |
| File persistence | 200 accumulate→checkpoint→reopen cycles | all 200 visible on final reopen |
| Independent DBs | 8 in parallel × 200 ops | isolated, no cross-talk |
| Same-handle concurrency | 8 tasks × 150 ops | 600 ok / 600 diagnosed / 0 unexpected |
| Dispose race | 20 rounds × 4 threads vs `Dispose` | only `ObjectDisposedException`, no unsafe failure |

## Native loader matrix

All cases terminate as `MinigrafNativeLoadException` with RID, architecture,
expected filename and probed locations; no PATH scan, no fallback:

| Case | Result |
| --- | --- |
| nonexistent file | fail closed |
| zero-byte file | fail closed |
| random-byte file | fail closed |
| directory override without asset | fail closed |
| expected filename is a directory | fail closed |
| missing parent directory | fail closed |
| wrong-architecture asset (override) | fail closed |
| PATH contains a rogue `minigraf.dll` | ignored |
| cwd contains a rogue `minigraf.dll` | ignored (application base wins) |

## Development override contract

`MINIGRAFSHARP_NATIVE_PATH` is a development/user escape hatch and is
**authoritative**: when set, resolution does not fall through to the packaged
asset. Verified behavior:

| Override | Result |
| --- | --- |
| valid staged asset | loads and runs |
| nonexistent path | `MinigrafNativeLoadException`, no fallthrough |
| corrupt file | `MinigrafNativeLoadException` |
| wrong architecture | `MinigrafNativeLoadException` |

The override asset is **externally supplied and not provenance-verified by
MinigrafSharp**; unlike the packaged asset it has no hash/manifest guarantee.
M2.5 adds no runtime hash verification for it.

## Package tamper matrix (verifier self-test)

`eng/Test-PackageVerifier.ps1` builds tampered variants and asserts
`eng/Verify-Package.ps1` rejects each; the untampered package passes as a
control. Verifier checks now also reject duplicate archive entries.

| Tamper | Verifier (expected / actual) |
| --- | --- |
| remove native dll | reject / reject |
| modify native dll byte | reject / reject |
| replace native dll with garbage | reject / reject |
| rename native entry path | reject / reject |
| remove managed net8 dll | reject / reject |
| remove managed net10 dll | reject / reject |
| remove LICENSE | reject / reject |
| remove NOTICE | reject / reject |
| add unexpected RID asset | reject / reject |
| duplicate native entry | reject / reject |

## Resource evidence

Bounded, non-fragile observations only:

- 5,000 in-memory open/dispose cycles complete; process handle count did not
  grow without bound (observed delta within noise, well under the 256 bound
  asserted).
- 200 file-backed cycles leave every database file deletable; all temp
  directories were removed and no child processes survived.
- No claim of formal leak-freedom is made. Conclusion:
  **no unbounded handle/resource growth was observed in the qualified
  stress workload.**

## Privacy review

| Path | Exposes | Notes |
| --- | --- | --- |
| `MinigrafResultException` | no raw result JSON | verified by sentinel test |
| `MinigrafExecutionException` | native diagnostic | does **not** echo caller input in the tested path (sentinel absent) |
| `MinigrafOpenException` | requested path | intentional developer-facing context |
| `MinigrafNativeLoadException` | RID, arch, filename, probed paths, pinned version | intentional; probed paths may include user/temp paths |
| argument exceptions | the argument value/null | standard |

No automatic console/file/debug logging exists. Exceptions returned to the
caller are intentional, not unsolicited logging.

## Legal/package inventory

Package `0.1.0-preview.2` contains exactly: `lib/net8.0` + `lib/net10.0`
`MinigrafSharp.dll` (+ xml), `runtimes/win-x64/native/minigraf.dll`,
`README.md`, `LICENSE`, `NOTICE`, nuspec, and NuGet's own
`[Content_Types].xml`/`_rels`/`.psmdcp`. No test binaries, recovery probes,
staging archives, VCS data, or secrets.

- MinigrafSharp: Apache-2.0.
- Minigraf / minigraf-c: MIT OR Apache-2.0 (attribution in NOTICE).
- `minigraf.dll` depends only on Windows/CRT libraries
  (`KERNEL32`, `ntdll`, `bcryptprimitives`, `VCRUNTIME140`, `api-ms-win-crt-*`)
  — no developer-only or local-path dependency.

The `.nuspec` inside the package confirms id, preview version, Apache-2.0
expression, project/repository URLs, both TFM groups, and **zero managed
dependencies**.

## Known remaining risks / non-blocking

- Only `win-x64` is packaged/qualified; other RIDs deferred (needs execution).
- Upstream Minigraf v2.0.5 storage limitations remain (see
  [v2-qualification.md](v2-qualification.md)).
- `MINIGRAFSHARP_NATIVE_PATH` override is unverified (documented above).
- Native-load diagnostics are developer-facing and include probed paths.
- No `net8.0` NativeAOT claim.
- The repository commit embedded in the nuspec is the commit the package was
  built from; build the release artifact from a clean committed tree.

## Preview release decision

See the final report / [verification.md](verification.md) M2.5 section for the
recorded decision.
