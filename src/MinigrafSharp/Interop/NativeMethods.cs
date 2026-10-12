using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MinigrafSharp.Native;

namespace MinigrafSharp.Interop;

/// <summary>
/// M0 lifecycle plus M1a raw-execution subset of the official minigraf-c ABI
/// (<c>native/include/minigraf.h</c> at minigraf-c v2.0.5).
/// Source-generated, internal, Cdecl. Raw handles and pointers never leave
/// this class: callers wrap a non-null database result in
/// <see cref="Native.SafeMinigrafDatabaseHandle"/>, copy owned or borrowed
/// UTF-8 through <see cref="NativeUtf8"/>, and free owned execute results
/// with <c>minigraf_string_free</c> exactly once.
/// </summary>
internal static partial class NativeMethods
{
    private const string LibraryName = "minigraf";

    // C ABI: MiniGrafDb *minigraf_open_in_memory(void);
    // Returns NULL on error. Ownership transfers to the caller.
    [LibraryImport(
        LibraryName,
        EntryPoint = "minigraf_open_in_memory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MinigrafOpenInMemory();

    // C ABI: void minigraf_close(MiniGrafDb *handle);
    // Called exactly once per owned handle via SafeHandle.ReleaseHandle.
    [LibraryImport(
        LibraryName,
        EntryPoint = "minigraf_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MinigrafClose(nint handle);

    // C ABI: char *minigraf_execute(MiniGrafDb *handle, const char *datalog);
    // The SafeHandle parameter keeps the database alive for the call.
    // Datalog input marshals as UTF-8. Non-null result is caller-owned and
    // must be freed with minigraf_string_free; NULL means failure (read
    // minigraf_last_error on the same handle).
    [LibraryImport(
        LibraryName,
        EntryPoint = "minigraf_execute",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MinigrafExecute(
        SafeMinigrafDatabaseHandle handle,
        string datalog);

    // C ABI: void minigraf_string_free(char *s);
    // Releases only strings returned by minigraf_execute. NULL is safe.
    [LibraryImport(
        LibraryName,
        EntryPoint = "minigraf_string_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MinigrafStringFree(nint s);

    // C ABI: const char *minigraf_last_error(MiniGrafDb *handle);
    // Returns a borrowed pointer: copy immediately, never free, valid only
    // until the next call on the same handle. May return NULL.
    [LibraryImport(
        LibraryName,
        EntryPoint = "minigraf_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint MinigrafLastError(
        SafeMinigrafDatabaseHandle handle);
}
