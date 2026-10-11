namespace MinigrafSharp;

/// <summary>
/// Owns one native <c>MiniGrafDb*</c>. The handle is closed through
/// <c>minigraf_close</c> exactly once during disposal.
/// </summary>
public sealed class MinigrafDatabase : IDisposable
{
    private Native.SafeMinigrafDatabaseHandle? _handle;
    private bool _disposed;

    private MinigrafDatabase(Native.SafeMinigrafDatabaseHandle handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Opens an in-memory Minigraf database using the pinned native engine.
    /// </summary>
    /// <exception cref="MinigrafNativeLoadException">The native library could not be loaded.</exception>
    /// <exception cref="MinigrafOpenException">The native open returned NULL.</exception>
    public static MinigrafDatabase OpenMemory()
    {
        Native.NativeLoader.EnsureLoaded();
        nint raw = Interop.NativeMethods.MinigrafOpenInMemory();
        if (raw == nint.Zero)
        {
            throw new MinigrafOpenException(
                "minigraf_open_in_memory returned NULL. " +
                "minigraf-c v2.0.5 exposes no error handle for failed opens, " +
                "so no further native diagnostic is available.");
        }

        return new MinigrafDatabase(new Native.SafeMinigrafDatabaseHandle(raw));
    }

    /// <summary>
    /// Releases the native database. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle?.Dispose();
        _handle = null;
    }
}
