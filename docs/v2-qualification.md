# Minigraf v2.0.5 qualification

Reliability boundary of MinigrafSharp when using the pinned engine.
M1d reproduces, classifies, and documents behavior; it adds no wrapper
workarounds and no public API. Probes live in
`tests/MinigrafSharp.QualificationTests` and run the real engine on
`win-x64`.

- Engine: Minigraf `2.0.5` / minigraf-c `2.0.5`, file format `v7`
  (`src/storage/mod.rs: pub const FORMAT_VERSION: u32 = 7`).
- Platform: `win-x64`, Windows 10.0.26200, .NET 8 and .NET 10.
- Evidence and commit identity: `docs/verification.md` (M1d evidence).

Status vocabulary:

- **qualified** — deterministic, asserted by a passing test.
- **observed** — reported by a probe; not asserted as a guarantee.
- **upstream limitation** — reproduced engine behavior the wrapper does
  not and cannot repair.
- **not reachable** — the issue exists in the core but the v2.0.5
  minigraf-c surface cannot express it.

## Matrix

| Area | v2.0.5 result | Status | User impact | v3 requalification |
| --- | --- | --- | --- | --- |
| Normal committed persistence | works | qualified | normal use supported | rerun |
| Explicit `Checkpoint()` | works (0 = success) | qualified | supported | rerun |
| Clean dispose flush | WAL present while open, removed on dispose (close-time checkpoint) | qualified | supported | rerun |
| Abrupt exit after returned write | committed write recovered 5/5 | qualified/limited | process kill, not power loss | rerun |
| Abrupt exit during write/checkpoint loop | reopens cleanly, prior data survives, post-kill checkpoint never shrinks the fact set (5/5) | qualified/limited | no exact in-checkpoint crash point claimed | verify COW/atomic save |
| Same-tx multi-valued facts (#371) | 2 values in one transact read back as 1; split transactions read 2; checkpoint+reopen rebuild restores both | upstream limitation | avoid same-tx multi-values | must verify fixed |
| Retract then re-assert in one tx (#477) | Datalog `retract`+`transact` keeps the fact | not reachable | no impact via minigraf-c | must verify if C API grows write txs |
| Valid-time replacement (#435) | overlapping windows both remain visible (no replacement) | upstream limitation | history accumulates; no window closing | must verify fixed |
| Old graph + newer WAL (#535) | accepted; newer WAL replays over the restored graph | upstream limitation | restoring `.graph` is unsafe while a `.wal` exists | must verify refusal |
| Truncated/short file | 100 bytes of a valid file → open fails (`MinigrafOpenException`); never a silent empty DB | qualified | corrupt files fail closed | rerun |
| Torn WAL | half-truncated WAL → opens; checkpointed generation visible, uncheckpointed entries lost | observed/limited | last uncheckpointed tx may be lost | rerun |
| `:valid-at` window reads | before/inside/after/any = 0/1/0/1 | qualified | supported | rerun |
| `:as-of` transaction time | `:as-of 1` returns the first-transaction view | qualified | supported | rerun |
| Recursive rules | derive correctly; rules are session-local (re-register after reopen) | observed/qualified | re-declare rules per opened database | rerun |
| Point lookup (`[?e :attr literal]`) | works (single and multi-valued, in-memory and after reopen) | qualified | supported | rerun |
| WAL-sidecar naming | `<db>.wal` beside the database path | qualified | supported | rerun |

## Details

### Multi-valued facts (#371)

```
(transact [[:e1 :tag "A"] [:e1 :tag "B"]])   ; same tx
-> current view: 1 row

(transact [[:e2 :tag "A"]])
(transact [[:e2 :tag "B"]])                  ; split tx
-> current view: 2 rows

checkpoint + reopen, then query e1
-> 2 rows
```

The live index view loses one value written in the same transaction; the
data is intact and the checkpoint/reopen index rebuild restores both. The
probe asserts `1 -> 2` and fails with a requalify message if either side
changes.

### Valid-time windows (#435)

```
transact {:valid-from 2023-01-01 :valid-to 2023-06-30} status :active
transact {:valid-from 2023-03-01 :valid-to 2023-09-30} status :inactive
:valid-at 2023-04-01 -> 2 rows
```

A later overlapping window does not replace the earlier one. Observations
are identical before and after `Checkpoint()`.

### Old graph + newer WAL (#535)

Choreography: advance the graph to generation B, then restore the
generation-A file over it while retaining the generation-C WAL. v2.0.5
opens the mismatched pair and replays the WAL. The probe logs a manifest
(name, size, SHA-256) of the fixtures it manipulates. All file
manipulation happens under the test-owned temp root, guarded by an
assertion that refuses any path outside it, and the probe fails with a
requalify message if a future engine refuses the mismatch.

### Checkpoint window

The native checkpoint is synchronous and opaque, so a probe cannot be
interrupted *inside* it. The Q4 probe runs a long write/checkpoint loop,
kills the child at a random point between managed operations, and then
asserts the deterministic property: the database reopens, prior
checkpointed data survives, and a post-kill checkpoint never reduces the
visible fact set. Observed kills landed between 78 and 279 iterations
into the loop. No exact in-checkpoint crash point is claimed.

## What callers should avoid on v2.0.5

These are engine behaviors, not wrapper preferences. MinigrafSharp does
not enforce them.

- Do not assert multiple values of one attribute for one entity in a
  single transaction if all values must be immediately queryable; write
  each value in its own transaction.
- Do not rely on a later valid-time assertion closing/replacing an earlier
  window; both remain visible.
- Do not restore an older `.graph` while a newer `<db>.wal` is present.
- Do not assume recursive rules survive reopen; re-declare them per open.

## Deferred to the v3 upgrade gate

- Confirm the same-transaction multi-value fix.
- Confirm valid-time window replacement rules.
- Confirm WAL-generation mismatch refusal.
- Confirm crash-atomic save/checkpoint behavior.
- Re-run this whole matrix against the v3 engine and update this file.

## v2.0.7 rerun (2026-10-17, win-x64)

Pin refresh maintenance checkpoint: Minigraf/minigraf-c moved from v2.0.5
to v2.0.7 (`native/upstream.json` carries the exact commits, header hash
and all five RID asset hashes). The C ABI is unchanged: the upstream
v2.0.7 `minigraf.h` is hash-identical (`8603C074…ADE7D`) to the archived
v2.0.5 header. Environment: Windows 11 x64, .NET SDK 10.0.401, TFMs
net8.0/net10.0, staged `minigraf.dll` binary SHA-256
`5FB9B8C8…0B607` (2150400 bytes). All three harnesses (unit, integration,
qualification) passed on both TFMs. Evidence
doc: `docs/verification.md` ("Maintenance checkpoint (2.0.7 pin refresh)
evidence" section, commit-bound).

Statuses, in the M1d vocabulary:

- `observed fixed` — #608 (rule calls with a literal argument): the exact
  upstream `magic_sets_test.rs` fixtures at v2.0.7 ran as the upstream
  Q-rule probe group. `(chain ?x :c)` returned exactly the producers
  (rows `:a,:b`); `(link ?x :c) (link ?p ?q)`, `(link :a ?y) (link ?p ?q)`
  and `(link ?y :c) (any ?p ?q)` all kept every edge (rows `:b|:a|:b` and
  `:b|:b|:c`); the `or`-branch and `not`-branch call shapes and a fully
  bound call beside a free call behaved as upstream asserts. Keyword
  entities were normalized through the upstream UUIDv5(OID, `":kw"`)
  convention (verified against the engine before use).
- `observed fixed` — #610 (`or` sharing no variable with earlier clauses):
  the exact upstream `disjunction_test.rs` fixtures at v2.0.7 ran as the
  Q-or probe group. Cross products of 4 rows, order-independent; the
  matched-branch case returns 2 rows with the one `?p` value joined
  against 2 `?z` values; the no-match branch pair returns 0 rows; the
  `not`-branch seeded path and the rule-body `or` cross product; the
  `:db/tx-count` pseudo-attribute probe joins. Caller-visible change vs
  v2.0.5: these queries previously returned empty or too-few rows; stored
  data was never affected (upstream statement).
- `not reachable through the wrapper's serialized per-database surface` —
  #554, #555, #558 (cross-thread partial-transaction visibility, mixed
  state across query patterns, facts missing during checkpoint). The
  wrapper serializes execute and checkpoint per handle (per-database
  `_sync` gate), so the public managed surface cannot interleave them; the
  M1d runs did not observe them and the wrapper surface still cannot
  express the racing interleaving. Engine-side fact from the upstream
  v2.0.6 release notes: all three were fixed in engine v2.0.6; this pin
  bump adopts that engine state. Not measured through any wrapper surface;
  recorded as adopted core-side facts, not wrapper-surface observations.
- `not reachable` through the official C surface — #603 (prepared queries
  dropping a literal `:as-of`/`:valid-at`/`:any-valid-time` when binding
  slots; fixed in v2.0.7 core): no prepared-statement function exists in
  any published minigraf-c, including v2.0.7, so this cannot be reached
  through the wrapper today. Core-side facts recorded from exact v2.0.7
  source (`src/query/datalog/prepared.rs` compared against v2.0.5: the
  delta is the #603 fix) and the release notes. The managed-surface
  recheck is scheduled inside M3.1 through the prepared-query shim.
- `upstream limitation` (reconfirmed still open on 2.0.7 binaries, same
  probe results and status text as the v2.0.5 measurements): #371/#287
  (one of two same-transaction multi-values visible until
  checkpoint/reopen), #477 (not reachable via minigraf-c Datalog; core
  WriteTransaction-level issue), #435 (later window does not replace),
  #374 (overlapping windows both visible), #535 (older `.graph` accepted
  beside newer `.wal`; exact staged fixture SHA-256 checked). The
  "avoid" guidance in this file carries forward unchanged on v2.0.7.
- `upstream limitation` (fail-closed paths reconfirmed unchanged):
  truncated `.graph` fails open, zero/short files open as new empty,
  torn WAL on a copy opens with the checkpointed generation and loses the
  uncheckpointed entries, abrupt-termination kills preserve the database
  and prior checkpointed data (Q4 5/5, Q7 5/5).

RIDs: this rerun qualifies `win-x64` only. `linux-x64`, `linux-arm64`,
`osx-x64` and `osx-arm64` stay pending; nothing here re-marks them passed.
M1d v2.0.5 content above is historical evidence, kept verbatim.
