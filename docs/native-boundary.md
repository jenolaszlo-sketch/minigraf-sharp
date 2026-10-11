# Native boundary (M0)

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
from `SafeHandle.ReleaseHandle`). `Execute`, `Checkpoint` and error-string
handling arrive in M1.

## Ownership

- `minigraf_open_in_memory`: non-null result is wrapped in
  `SafeMinigrafDatabaseHandle`; NULL becomes `MinigrafOpenException`
  (minigraf-c exposes no error handle for failed opens).
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

## Supported RID (M0)

`win-x64` only (`minigraf.dll`, staged by `eng/Get-Native.ps1`, ignored by
git). Upstream also publishes `linux-x64`, `linux-arm64` and macOS
`universal2` (recorded in `native/upstream.json`); they qualify in M2.
