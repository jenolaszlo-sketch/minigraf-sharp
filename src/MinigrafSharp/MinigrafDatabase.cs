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
                "minigraf-c v2.0.7 exposes no error handle for failed opens, " +
                "so no further native diagnostic is available.");
        }

        return new MinigrafDatabase(new Native.SafeMinigrafDatabaseHandle(raw));
    }

    /// <summary>
    /// Opens a file-backed Minigraf database at <paramref name="path"/>,
    /// creating it when it does not exist. The path is passed to native
    /// Minigraf as supplied (no trimming or normalization) using UTF-8.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="MinigrafNativeLoadException">The native library could not be loaded.</exception>
    /// <exception cref="MinigrafOpenException">The native open returned NULL.</exception>
    public static MinigrafDatabase Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            throw new ArgumentException("Database path must not be empty.", nameof(path));
        }

        Native.NativeLoader.EnsureLoaded();
        nint raw = Interop.NativeMethods.MinigrafOpen(path);
        if (raw == nint.Zero)
        {
            // No handle exists on failure, so minigraf_last_error cannot be
            // consulted: the message stays honest about the missing detail.
            // The requested path is included: it is the one diagnostic the
            // managed side actually has for a failed open.
            throw new MinigrafOpenException(
                $"minigraf_open returned NULL for '{path}'. " +
                "minigraf-c v2.0.7 exposes no error handle for failed opens, " +
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
    /// Executes one Datalog statement and returns the typed managed result.
    /// Implemented as <see cref="ExecuteRaw"/> plus managed JSON decoding:
    /// there is no second native execution path.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="datalog"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="MinigrafExecutionException">The engine rejected the statement.</exception>
    /// <exception cref="MinigrafResultException">The native result has an unsupported shape.</exception>
    public MinigrafResult Execute(string datalog)
    {
        string json = ExecuteRaw(datalog);
        return MinigrafResultDecoder.Decode(json);
    }

    /// <summary>
    /// Flushes the write-ahead log to the database file using the official
    /// native checkpoint. Participates in the same per-database gate as
    /// execution, so it cannot race sibling operations, disposal, or the
    /// borrowed error capture below.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="MinigrafCheckpointException">The native checkpoint reported failure.</exception>
    public void Checkpoint()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            Native.SafeMinigrafDatabaseHandle handle = _handle!;

            int rc = Interop.NativeMethods.MinigrafCheckpoint(handle);
            if (rc != 0)
            {
                nint errorPtr = Interop.NativeMethods.MinigrafLastError(handle);
                string? message = Interop.NativeUtf8.ReadCString(errorPtr);
                if (string.IsNullOrEmpty(message))
                {
                    message = "Minigraf checkpoint failed without a native diagnostic.";
                }

                throw new MinigrafCheckpointException(message);
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
