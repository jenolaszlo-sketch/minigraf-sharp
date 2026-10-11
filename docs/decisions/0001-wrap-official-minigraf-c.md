# ADR 0001: Wrap the official minigraf-c directly

Status: accepted (M0).

## Decision

MinigrafSharp P/Invokes the official `minigraf-c` library. No
MinigrafSharp-owned Rust bridge.

## Reason

Minigraf already publishes a stable C FFI package with native assets, so a
second bridge would add native code and another compatibility boundary for
no M0 capability. The official boundary (`minigraf_open`,
`minigraf_open_in_memory`, `minigraf_close`, `minigraf_execute`,
`minigraf_string_free`, `minigraf_checkpoint`, `minigraf_last_error`) covers
the M0 lifecycle and the M1 surface; richer core features (prepared
queries, write transactions, open options) are deferred to upstream
`minigraf-c` additions first.

## Consequences

- Provenance pins minigraf-c `v2.0.5` exactly (versions, commits, header
  hash, asset hashes in `native/upstream.json`).
- A MinigrafSharp-specific native extension requires a future ADR before
  implementation and must wrap the official core crate with its own ABI
  version (product spec §4.3).
