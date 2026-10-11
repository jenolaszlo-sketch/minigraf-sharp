# Roadmap

Derived from `docs/product-spec.md` (authoritative). Only actually
completed items are marked complete.

- [x] M0 - native lifecycle (solution, pinned baseline, `OpenMemory()` smoke)
- [ ] M1 - first usable binding (`Open`, `Execute`/`ExecuteRaw`, typed results,
  `Checkpoint`, error taxonomy, temporal/persistence/concurrency tests, QuickStart)
- [ ] M2 - distribution gate (verified native assets per qualified RID, NuGet
  package, hash verifier, clean consumer, .NET 8/10 matrix, trimming, NativeAOT smoke)
- [ ] M2.5 - hardening (fault paths, stress, tamper tests, known-issue matrix,
  cross-binding fixtures, API freeze)
- [ ] M3 - richer native surface (prepared queries, write transactions, open
  options, structured errors; prefer upstream minigraf-c additions)
- [ ] M4 - agent-memory conveniences, optional and separate from the binding
