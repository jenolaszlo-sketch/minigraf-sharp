# M3.2 plan (checkpoint record)

Status: M3.2a executed; ADR 0003 accepted; M3.2b implemented and locally qualified (cross-RID evidence pending the slice's CI run); M3.2c-g pending. Baseline is the
frozen M3.1 release: `0.1.0-preview.4`, qualified source `418e9fc`,
post-release correction `ff5a7aa`, package SHA-256
`D1F22A7D98CC50B3711572927BCC03F236391A87A2758D210B989E4CCC875D86`
(win-x64/linux-x64/osx-arm64; `linux-arm64`/`osx-x64` unqualified).

## Slices

| Slice | Content | Gate |
| --- | --- | --- |
| M3.2a | Native capability characterization against pinned Minigraf 2.0.7 (source audit + measured probes) | this record + `characterization.md` |
| ADR 0003 | Rich native surface design: ABI additions, ownership, state machine, error/value/options/identity models, exclusions | user approval before any public managed type |
| M3.2b | Explicit write transactions (shim ABI + managed type) | ADR approved; native + managed qualification |
| M3.2c | Open options (typed subset with direct native meaning) | ADR approved; per-option qualification matrix |
| M3.2d | Structured native errors (category + stable code + diagnostic preserved) | ADR approved; unknown path proven |
| M3.2e | Typed Ref/Keyword values (decode + round trip; prepared-bind schema already typed) | ADR approved; M3.1 prepared requalification if decode changes |
| M3.2f | Native/core identity exposure (complements M3.1 shim identity) | ADR approved; mismatch negative control |
| M3.2g | Integrated qualification: M3.1 regression + new matrix on all three RIDs, net8/net10, NativeAOT, packaging, independent review | full matrix green; evidence commit-bound |

## Discipline carried from M3.1

- Measure the pinned core first; expose only what can be owned and
  qualified; no managed invention of native semantics.
- M3.1 prepared-query semantics stay frozen; any decode enrichment is an
  explicitly approved ADR decision with rerun qualification.
- Qualified RID set unchanged; no cross-builds; `ROADMAP.md` untouched;
  no publication without separate dispatch.
- Native ABI closure: the M3.1 export inventory plus reviewed additions,
  with the generated header authoritative and its hash recorded.
- Evidence: `docs/evidence/m3.2/` files trace every claim to source
  revision, CI run, native artifact and probe.

## Current run state (M3.2a)

- 16 characterization probes in `native/shim/tests/characterization.rs`
  (run by `cargo test --locked`, i.e. inside every `dist` job).
- Source audit pins: crates.io `minigraf 2.0.7` tarball SHA-256
  `74E6724A59FECE46F0F56C3FE16F1792B34E34FE08FDFF4C902B22696D760D68`
  (verified locally against the downloaded crate; 1443572 bytes);
  minigraf-c 2.0.7 reference `official-c-207-lib.rs` SHA-256
  `B14CA2F167B989309310A5052B76E30E9072A6534FD5A22A1DF52F767E8B65B9`
  (10174 bytes, matches the M3.1a record's `B14CA2F1…B65B9`).
