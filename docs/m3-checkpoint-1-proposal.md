# Maintenance checkpoint proposal — pin refresh to Minigraf/minigraf-c v2.0.7 (no ABI change)

Status: proposal, awaiting review before any implementation.
Written: 2026-10-17.

Rescoped by review decision of 2026-10-17: this is a separate maintenance
and requalification checkpoint. It is not M3 and not M3.1; it does not
count toward the M3 milestone items in `ROADMAP.md`. M3.1 is prepared
queries over an owned native shim (ADR 0002 draft /
`docs/decisions/0002-prepared-queries-owned-shim.md`).

## Objective

Deliver the maintenance checkpoint before any M3.1 implementation:
refresh the pinned native baseline from Minigraf/minigraf-c `v2.0.5` to
the newest published v2.x pair (`v2.0.7`, both), with no C ABI change,
no wrapper API change, and a full requalification of the existing suite
plus new positive-control probes for the upstream query-integrity fixes
that flow through `Execute`.

## Ordering and M3.1 dependency (review decision 1, resolved here)

This checkpoint is completed before any M3.1 implementation starts. Its
requalification produces several facts that ADR 0002
(`docs/decisions/0002-prepared-queries-owned-shim.md`) requires as
measurements rather than assumptions:

- the exact post-refresh pinned core version, commit and checksums
  recorded in `native/upstream.json` — the shim's dependency is restated
  against these facts and this ADR 0002 is marked revalidated only after
  they exist;
- explicit rechecks of #554 and #555 and #558 on the 2.0.7 binaries
  (their status follows the measurement; see scope item 6);
- core-side verification of #603 at the refreshed tag (source-level
  comparison of `src/query/datalog/prepared.rs` and the upstream release
  notes); the managed-surface recheck of #603 happens inside M3.1,
  through the prepared-query probes of the shim.

M3.1 remains blocked — for implementation, not for approval — until this
checkpoint's evidence lands and ADR 0002 cites it.

## Upstream audit (observed 2026-10-17)

Source of truth at proposal time (to be re-verified with exact hashes at
execution time, per repo evidence standards):

- minigraf-c released `v2.0.6` (2026-10-14) and `v2.0.7` (2026-10-16).
- Both releases' `include/minigraf.h` are byte-identical in text to the
  archived v2.0.5 header (`native/include/minigraf.h`, SHA-256
  `8603C0745EDE143BC9C231E2812E7BCDC2E9B37186CF3E99E8E8F9AE442ADE7D`):
  the same 7 functions (`open`, `open_in_memory`, `close`, `execute`,
  `string_free`, `checkpoint`, `last_error`). No C ABI change between
  v2.0.5 and v2.0.7.
- Engine `v2.0.6` fixes three cross-thread data-integrity bugs
  (#554 partial-transaction visibility, #555 mixed state across query
  patterns, #558 queries missing facts during a checkpoint).
- Engine `v2.0.7` fixes #608 (rule calls with a literal argument returned
  every row or silently too few rows), #610 (an `or` sharing no variable
  with earlier clauses returned no rows instead of the cross product), and
  #603 (a prepared query dropped a literal `:as-of`/`:valid-at`/
  `:any-valid-time` when binding slots).
- All other v2.x known issues remain open on the 2.x line: #371/#287,
  #477, #435, #374, #535. Upstream states they are fixed in `v3.0.0`.
- `v3.0.0` is not published (engine releases top out at `v2.0.7`) and no
  minigraf-c v3-line binding exists. M1d evidence stays correct as-is.

## Why the rest of M3 is still blocked upstream

Every remaining M3 item (prepared queries with bind slots, explicit write
transactions, open options, structured error category/code, typed
Ref/Keyword values, native version/ABI identity) requires C functions that
do not exist in any published minigraf-c release, including v2.0.7. The
product spec forbids the wrapper-side substitutes: no emulated
transactions, no home-grown parameter syntax, no parsing English error
text, no inferring Ref/Keyword identity from string shapes, no unenforceable
open options (product-spec §10, §11, §12, §13.2, §9.4). Per §29's policy,
a MinigrafSharp-owned ABI extension requires an approved ADR plus real
consumer demand first — that is exactly the maintenance-then-M3.1
ordering this document executes at the maintenance layer, with the owned
extension defined in ADR 0002.

# Exact scope

1. `native/upstream.json` — bump both pins to `v2.0.7` (versions, tags,
   commits: engine `2d2e5d7…`, minigraf-c commit captured from its release
   at execution time), recompute the header SHA-256 against the upstream
   raw v2.0.7 header, and refresh all five recorded RID asset SHA-256s
   from the release digests. Win-x64 becomes the qualified RID after the
   local requalification run; all other RIDs stay recorded-but-not-quote-
   qualified (pending), never marked passed.
2. `native/include-minigraf.h` handling — re-archive the upstream v2.0.7
   header (expected byte-identical; the hash check is the acceptance
   proof) and record both hashes in the docs.
3. Pin identity in code (mechanical, no behavior change):
   `NativeLoader.PinnedMinigrafVersion/PinnedMinigrafCVersion` → `2.0.7`;
   update version strings in `MinigrafDatabase.cs` open-failure messages,
   `NativeMethods.cs` header comment, `MinigrafExceptions.cs`,
   `MinigrafResultDecoder.cs` to say minigraf-c v2.0.7.
4. `tests/MinigrafSharp.QualificationTests/Program.cs` — bump the version
   gate strings to `2.0.7` (still reading the pins from `upstream.json`),
   and add two new probes with the exact upstream-release-note fixtures as
   positive controls:
   - Q-rule (#608): a rule call with a literal argument whose results were
     previously lost/miscomputed, plus the `INT-022` failing case; each
     must now return the upstream-correct rows.
   - Q-or (#610): `[?x :edge ?z] (or [?p :edge :b] [?p :edge :c])` must
     return its cross product; `or-join` must still reject a join variable
     no earlier clause binds (`INT-018` control unchanged).
   Existing Q1 (#371), #477, and #535 tripwires stay: they must still show
   the known 2.x behavior. #603 is classified `not reachable` through the
   official C ABI (no prepared statements) and recorded, not probed here:
   this checkpoint records the core-side fix facts from exact v2.0.7
   source (`src/query/datalog/prepared.rs` compared against v2.0.5) and
   the release notes, and the managed-surface #603 recheck is an M3.1
   probe (via the shim's prepared queries), per the ordering section.
5. Pin/version mentions in `eng/Get-Native.ps1` (or its packaging
   constants) — restage `native/staging/win-x64/minigraf.dll` from the
   v2.0.7 release and verify the recorded SHA-256.
6. Docs, identity-and-evidence updates only (append-only for M1d facts):
   `docs/native-boundary.md`, `docs/compatibility.md`, `docs/hardening.md`,
   `docs/packaging.md`, `README.md`, `CHANGELOG.md` version lines.
   `docs/v2-qualification.md` gains a `2.0.7 rerun` section using the
   existing status vocabulary (`observed`, `observed fixed`,
   `upstream limitation`, `not reachable`): every M1d probed issue rerun on
   the 2.0.7 binaries; #554/#555/#558 recorded as `not reachable through
   the wrapper's serialized per-database surface` (the wrapper serializes
   execute and checkpoint per handle, so the public surface cannot
   interleave them; the 2.0.6 engine-side fix is adopted by the pin bump
   and stated as measured core-side facts from the upstream release
   notes, not as wrapper-surface observations); #603 as `not reachable`
   through the official C surface (core-side facts recorded; managed
   recheck inside M3.1); #608/#610 as `observed fixed` with exact fixture
   output; #371/#287, #477, #435, #374, #535 reconfirmed still open.
   ADR 0002 cites these measured statuses; none is carried forward from
   v2.0.5 by assumption.
7. `ROADMAP.md` — add a dated sub-note under the M3 row
   ("2.0.7 pin refresh, qualification evidence recorded; richer surface
   still blocked upstream (no C API for items 1-6)"). The pre-existing
   uncommitted user edit is left exactly untouched.

## Non-goals

- No new public API. No `MinigrafPreparedQuery`, bind values, write
  transactions, open options, structured errors, or Ref/Keyword kinds.
- No MinigrafSharp-owned ABI extension or ADR in this slice.
- No dependency on v3.0.0 (unpublished; no C binding). No attempt to parse
  engine error text or repair inherited upstream limitations.
- No changes to Gagamba or any other Penghou project.
- No new RID qualification claims; unqualified RIDs stay pending.
- No code refactoring beyond the mechanical pin-identity updates above.
- `docs/v2-qualification.md` M1d content is appended to, never rewritten.

## Invariants

- Provenance pins stay exact: versions, commits, header hash, asset
  hashes recorded and verified at execution time.
- The version gate continues to read pins from `native/upstream.json` and
  rejects any mismatch before probing.
- Every accessible state remains explicit; no fallback behavior added.
- Public API surface and exception taxonomy unchanged.
- M1d qualification facts and their commit binding remain untouched.

## Acceptance criteria

1. `native/upstream.json` names exactly `v2.0.7` for both projects; the
   archived header hash equals the upstream v2.0.7 header hash
   (byte-identical expected; both hashes recorded side by side).
2. Restaged `win-x64` binary SHA-256 matches the upstream release digest.
3. All three test programs run green on win-x64 with the v2.0.7 staged
   binary; the bonus qualification probes pass asserts for #608/#610 and
   the not-yet-fixed tripwires pass.
4. A new evidence doc records exact OS/CPU/SDK identity and commit, the
   versions tested, the hashes, every check with its exact result
   including any failure mode, and cleanup checks (no test fixtures left
   open).
5. Each doc or code identity line updated is listed in the evidence doc;
   no doc claims qualification of an unqualified RID.
6. No other repo file is modified outside scope.

## Compatibility constraints

- File format stays `v7`; nothing changes on disk semantics in scope.
- Public API remains provisional upstream; no public API refresh here.
- Consumer-visible result change: previously-empty or rows that were
  silently missing for #608/#610 queries now return correct rows — this is
  correct upstream behavior, noted explicitly as the only observable
  difference (upstream says stored data was never affected).
- Previously "known setup blocked"-style guidance in compatibility docs
  stays as-is, now citing v2.0.7 where v2.0.5 was named.
- Unqualified RIDs (`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`)
  remain pending for this scope, per the evidence standards.

## Review gate

Stop after writing this proposal and before touching code. This slice
lands only after review of (a) the chosen slice, (b) probe fixtures, and
(c) the claim that items 1-6 of M3 stay blocked upstream.
