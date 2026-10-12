namespace MinigrafSharp;

/// <summary>
/// Owns one native <c>MiniGrafDb*</c>. The handle is closed through
/// <c>minigraf_close</c> exactly once during disposal.
/// </summary>
public sealed class MinigrafDatabase : IDisposable
{
    // Serializes execute/error-copy sequences on this database.
    // minigraf_last_error is borrowed mutable diagnostic state on the handle:
    // another call on the same handle may replace it before managed code
    // copies it, and the native mutex does not cover the gap between the two
    // C calls. Never a global lock: independent databases proceed in parallel.
    private readonly object _sync = new();
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
    /// Executes one Datalog statement and returns the raw native JSON result
    /// as a managed string, byte-identical in content (no normalization).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="datalog"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="MinigrafExecutionException">The engine rejected the statement.</exception>
    public string ExecuteRaw(string datalog)
    {
        ArgumentNullException.ThrowIfNull(datalog);

        lock (_sync)
        {
            ThrowIfDisposed();
            Native.SafeMinigrafDatabaseHandle handle = _handle!;

            nint result = Interop.NativeMethods.MinigrafExecute(handle, datalog);
            if (result == nint.Zero)
            {
                throw CaptureExecutionError(handle);
            }

            try
            {
                return Interop.NativeUtf8.ReadCString(result)
                    ?? throw new MinigrafExecutionException(
                        "minigraf_execute returned a null string.");
            }
            finally
            {
                Interop.NativeMethods.MinigrafStringFree(result);
            }
        }
    }

    /// <summary>
    /// Releases the native database. Idempotent. Waits for any in-flight
    /// execution on this database rather than racing it.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private static MinigrafExecutionException CaptureExecutionError(
        Native.SafeMinigrafDatabaseHandle handle)
    {
        // Borrowed pointer: copy before any other call on this handle can
        // replace it, then never free it. The per-database gate above keeps
        // this execute/error pair atomic against sibling calls.
        nint errorPtr = Interop.NativeMethods.MinigrafLastError(handle);
        string? message = Interop.NativeUtf8.ReadCString(errorPtr);
        if (string.IsNullOrEmpty(message))
        {
            message = "Minigraf execution failed without a native diagnostic.";
        }

        return new MinigrafExecutionException(message);
    }
}
