# Native boundary (M1a)

Pinned baseline: Minigraf core `v2.0.5`
(`a861e551162623c8f4752f94daf5b637543f8358`), minigraf-c `v2.0.5`
(`8422f9f048d8821a759cbe9e1c2ea6b8f53621dc`). Full provenance in
`native/upstream.json`; archived header `native/include/minigraf.h`
(SHA-256 `8603C0745EDE143BC9C231E2812E7BCDC2E9B37186CF3E99E8E8F9AE442ADE7D`).

## C functions (from the pinned header)

```c
MiniGrafDb *minigraf_open(const char *path);
MiniGrafDb *minigraf_open_in_memory(void);
void minigraf_close(MiniGrafDb *handle);
char *minigraf_execute(MiniGrafDb *handle, const char *datalog);
void minigraf_string_free(char *s);
int minigraf_checkpoint(MiniGrafDb *handle);
const char *minigraf_last_error(MiniGrafDb *handle);
```

M0 binds only `minigraf_open_in_memory` (returns owned handle, NULL on
error) and `minigraf_close` (releases one owned handle, called exactly once
from `SafeHandle.ReleaseHandle`).

M1a additionally binds, exactly matching the pinned header:

- `minigraf_execute(handle, datalog)`: `SafeHandle` parameter keeps the
  database alive for the call; Datalog input marshals as UTF-8
  (`StringMarshalling.Utf8`, never ANSI/codepage). Non-null result is
  caller-owned; NULL means failure.
- `minigraf_string_free(s)`: releases only `minigraf_execute` results,
  exactly once per success, in `finally`; NULL is safe.
- `minigraf_last_error(handle)`: borrowed, copied immediately after a
  failed execute before any other call on that handle, never freed; may
  itself be NULL (deterministic fallback message, never success).

`Checkpoint` is not bound (M1c owns it).

## Ownership

- `minigraf_open_in_memory`: non-null result is wrapped in
  `SafeMinigrafDatabaseHandle`; NULL becomes `MinigrafOpenException`
  (minigraf-c exposes no error handle for failed opens).
- `minigraf_execute` result: owned, copied to managed memory with strict
  UTF-8 decoding, freed with `minigraf_string_free` exactly once even if
  conversion throws. Never `Marshal.FreeHGlobal`/`FreeCoTaskMem`,
  `NativeMemory.Free`, CRT `free`, or any allocator but the paired native
  free from the retained library.
- `minigraf_last_error`: borrowed, copied immediately, never freed.
  The managed per-database gate keeps each failed-execute plus error-copy
  pair atomic against sibling calls on the same handle; the native mutex
  alone does not cover that gap.
- `minigraf_close`: runs once per handle on disposal; disposal is idempotent.
- The loaded library is never freed for the process lifetime.

## Loading

Order: `MINIGRAFSHARP_NATIVE_PATH` override (file or directory) ->
`<app>/runtimes/<rid>/native/<file>` -> `<app>/<file>` ->
repo `native/staging/<rid>/<file>` walk-up. No PATH scan, no download.
A missing asset throws `MinigrafNativeLoadException` with RID,
architecture, expected filename, probed locations and pinned versions.

Interop is source-generated `LibraryImport` (Cdecl) with a deterministic
`DllImportResolver` returning the pre-loaded handle.

## Supported RID (M1a)

`win-x64` only (`minigraf.dll`, staged by `eng/Get-Native.ps1`, ignored by
git). Upstream also publishes `linux-x64`, `linux-arm64` and macOS
`universal2` (recorded in `native/upstream.json`); they qualify in M2.
