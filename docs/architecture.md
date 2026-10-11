# Architecture (M0)

```text
consumer
   |
MinigrafDatabase (public, src/MinigrafSharp/MinigrafDatabase.cs)
   |
SafeMinigrafDatabaseHandle + NativeLoader (internal, src/MinigrafSharp/Native/)
   |
NativeMethods LibraryImport declarations (internal, src/MinigrafSharp/Interop/)
   |
official minigraf-c v2.0.5
   |
Minigraf v2.0.5
```

MinigrafSharp does not implement Minigraf semantics: Datalog parsing,
storage, temporal evaluation and query planning stay in the native engine.
M0 proves only the lifecycle boundary (`minigraf_open_in_memory` /
`minigraf_close`).

- No native handles are exposed publicly (`docs` unit test
  `no IntPtr/SafeHandle in public signatures` enforces this).
- Every owned `MiniGrafDb*` lives in exactly one `SafeHandle` whose release
  path calls `minigraf_close` once.
- The native library is loaded once per process and never unloaded.
- MinigrafSharp owns no Rust bridge (see
  [0001-wrap-official-minigraf-c.md](decisions/0001-wrap-official-minigraf-c.md)).
