using System.Runtime.InteropServices;
using MinigrafSharp.Interop;

namespace MinigrafSharp.Native;

/// <summary>
/// Owns one native shim <c>MgsDb*</c>. Release calls <c>mgs_close</c>
/// exactly once. Never exposed publicly.
/// </summary>
internal sealed class SafeMgsDatabaseHandle : SafeHandle
{
    internal SafeMgsDatabaseHandle(nint handle)
        : base(nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        ShimNativeMethods.MgsClose(handle);
        return true;
    }
}
