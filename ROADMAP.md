# Roadmap

Derived from `docs/product-spec.md` (authoritative). Only actually
completed items are marked complete.

- [x] M0 - native lifecycle (solution, pinned baseline, `OpenMemory()` smoke)
- [x] M1a - raw execution (`ExecuteRaw`, owned/borrowed string ownership,
  error capture, per-database gate)
- [x] M1b - typed result decoding (`Execute`, result/row/value model, strict decoder)
- [x] M1c - file-backed persistence (`Open(path)`, `Checkpoint`, close/reopen proof)
- [x] M1d - storage/recovery/temporal qualification (reproduce, classify, document
  known v2.0.5 limitations; no public API change)
- [x] M2 - distribution gate (NuGet preview package for the qualified `win-x64`
  RID, hash verifier, clean consumers on net8/net10, NativeAOT, trimming,
  package tamper/wrong-arch diagnostics). Other RIDs remain unqualified.
- [ ] M2.5 - hardening (fault paths, stress, tamper tests, known-issue matrix,
  cross-binding fixtures, API freeze)
- [ ] M3 - richer native surface (prepared queries, write transactions, open
  options, structured errors; prefer upstream minigraf-c additions)
- [ ] M4 - agent-memory conveniences, optional and separate from the binding
