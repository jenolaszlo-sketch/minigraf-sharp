using System.Runtime.InteropServices;
using MinigrafSharp.Interop;

namespace MinigrafSharp.Native;

/// <summary>
/// Owns one native <c>MiniGrafDb*</c>. Release calls
/// <c>minigraf_close</c> exactly once. Never exposed publicly.
/// </summary>
internal sealed class SafeMinigrafDatabaseHandle : SafeHandle
{
    internal SafeMinigrafDatabaseHandle(nint handle)
        : base(nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.MinigrafClose(handle);
        return true;
    }
}
