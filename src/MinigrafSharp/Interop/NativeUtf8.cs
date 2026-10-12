using System.Text;

namespace MinigrafSharp.Interop;

/// <summary>
/// Copies NUL-terminated native UTF-8 into managed strings. Narrowly scoped
/// to the two M1a cases: owned <c>minigraf_execute</c> results (caller frees
/// with <c>minigraf_string_free</c> after copying) and the borrowed
/// <c>minigraf_last_error</c> pointer (copy immediately, never free).
/// Decoding is strict: invalid UTF-8 throws instead of silently replacing.
/// </summary>
internal static class NativeUtf8
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Copies a NUL-terminated native UTF-8 string. Returns null only for a
    /// null pointer. Ownership is unchanged: owned pointers must still be
    /// freed by the caller, borrowed pointers must not be.
    /// </summary>
    internal static unsafe string? ReadCString(nint ptr)
    {
        if (ptr == nint.Zero)
        {
            return null;
        }

        byte* p = (byte*)ptr;
        int length = 0;
        while (p[length] != 0)
        {
            length++;
        }

        return StrictUtf8.GetString(new ReadOnlySpan<byte>(p, length));
    }
}
