using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MinigrafSharp.Interop;

/// <summary>
/// M0 lifecycle subset of the official minigraf-c ABI
/// (<c>native/include/minigraf.h</c> at minigraf-c v2.0.5).
/// Source-generated, internal, Cdecl. Raw handles never leave this class:
/// callers wrap a non-null result in <see cref="Native.SafeMinigrafDatabaseHandle"/>.
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
}
