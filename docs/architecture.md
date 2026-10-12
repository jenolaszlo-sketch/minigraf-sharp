# Architecture (M1c)

```text
consumer
   |
MinigrafDatabase (public, src/MinigrafSharp/MinigrafDatabase.cs)
   |
SafeMinigrafDatabaseHandle + NativeLoader (internal, src/MinigrafSharp/Native/)
   |
NativeMethods LibraryImport declarations
+ NativeUtf8 copy helper (internal, src/MinigrafSharp/Interop/)
   |
official minigraf-c v2.0.5
   |
Minigraf v2.0.5
```

MinigrafSharp does not implement Minigraf semantics: Datalog parsing,
storage, temporal evaluation and query planning stay in the native engine.
M0 proved the lifecycle boundary (`minigraf_open_in_memory` /
`minigraf_close`); M1a adds the raw execution boundary
(`minigraf_execute` / `minigraf_string_free` / `minigraf_last_error`).

## Owned native strings (M1a)

```text
minigraf_execute
     |
owned native UTF-8 (char*)
     |
managed copy (strict UTF-8 decode)
     |
minigraf_string_free, exactly once, in finally
     |
managed string
```

Every successful result is copied before it is freed, and freed even when
managed conversion throws. Only `minigraf_string_free` from the retained
library releases these pointers — never a .NET allocator or CRT `free`.

## Borrowed error strings (M1a)

`minigraf_last_error` returns a borrowed pointer: it is copied immediately
after a failed execute, before any other call on that handle, and never
freed. A null error pointer yields a deterministic fallback message, never
success.

## Per-database operation gate (M1a)

`MinigrafDatabase` serializes each execute/error-copy sequence on a
per-instance lock. `minigraf_last_error` is borrowed mutable diagnostic
state on the handle: another call on the same handle may replace it before
managed code copies it, and the native mutex does not cover the gap between
the execute and last-error C calls. This gate is a correctness requirement
around the C error contract, not a performance abstraction; independent
databases still proceed in parallel (no global lock). `Dispose` takes the
same gate, so it waits for an in-flight call instead of racing it.

## Typed decoding (M1b)

```text
native JSON
    |
ExecuteRaw (proven M1a boundary, unchanged)
    |
managed decoder (internal MinigrafResultDecoder, System.Text.Json)
    |
immutable typed results (MinigrafResult hierarchy)
```

`Execute()` is `ExecuteRaw()` plus decoding: no second native path, no
reimplemented ownership. The decoder retains no `JsonDocument` and never
exposes `JsonElement`; rows and variable lists are immutable snapshots.
Malformed or ambiguous native results fail with `MinigrafResultException`,
distinct from `MinigrafExecutionException` (engine rejected the call).

## Persistent lifecycle (M1c)

```text
Open(path)
   |
SafeHandle (same handle type as OpenMemory)
   |
Minigraf file (+ WAL beside it)
   |
Execute / ExecuteRaw (unchanged)
   |
Checkpoint (explicit WAL flush, same per-database gate)
   |
Dispose / Reopen (same committed state observable)
```

File-backed and in-memory databases share one class, one handle type, one
gate, and one close path. Crash recovery, WAL-mismatch, and known v2
storage-issue qualification are deferred to M1d; M1c proves only
normal-close persistence.

- No native handles are exposed publicly (`docs` unit test
  `no IntPtr/SafeHandle in public signatures` enforces this).
- Every owned `MiniGrafDb*` lives in exactly one `SafeHandle` whose release
  path calls `minigraf_close` once.
- The native library is loaded once per process and never unloaded.
- MinigrafSharp owns no Rust bridge (see
  [0001-wrap-official-minigraf-c.md](decisions/0001-wrap-official-minigraf-c.md)).
