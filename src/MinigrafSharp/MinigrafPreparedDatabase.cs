using System.Text.Json;

namespace MinigrafSharp;

/// <summary>
/// Owns one native shim <c>MgsDb*</c>. The handle is closed through
/// <c>mgs_close</c> exactly once during disposal. Prepared queries are
/// created by <see cref="Prepare"/> and explicit write transactions by
/// <see cref="BeginWrite"/>; both register with this database and are
/// cascade-closed during its own disposal, so no managed object outlives or
/// ambiguously owns the native state it depends on (ADR 0002/0003 ownership
/// discipline). This is the owned native surface: prepared queries (M3.1)
/// plus the rich native surface (M3.2); <see cref="MinigrafDatabase"/> stays
/// the official minigraf-c surface and is untouched.
/// </summary>
public sealed class MinigrafPreparedDatabase : IDisposable
{
    // Serializes prepare/execute/error-copy/disposal/cascade sequences on
    // this database, exactly the per-database gate of MinigrafDatabase:
    // borrowed shim diagnostics are handle-local mutable state, and no
    // global lock is added (independent databases proceed in parallel).
    // Internal so prepared queries share the same gate.
    internal readonly object _sync = new();
    private readonly List<MinigrafPreparedQuery> _children = [];
    private Native.SafeMgsDatabaseHandle? _handle;
    private MinigrafWriteTransaction? _writeTransaction;
    private bool _disposed;

    private MinigrafPreparedDatabase(Native.SafeMgsDatabaseHandle handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Opens an in-memory Minigraf database engine using the owned shim.
    /// Loads and identity-verifies the shim first, so an identity mismatch
    /// fails here instead of on first use.
    /// </summary>
    /// <exception cref="MinigrafNativeLoadException">The shim could not be
    /// loaded or its identity token did not match the pinned provenance.</exception>
    /// <exception cref="MinigrafOpenException">The shim open returned NULL.</exception>
    public static MinigrafPreparedDatabase OpenMemory()
    {
        Native.ShimNativeLoader.EnsureLoadedAndVerified();
        nint raw = Interop.ShimNativeMethods.MgsOpenInMemory();
        if (raw == nint.Zero)
        {
            throw new MinigrafOpenException(
                "mgs_open_in_memory returned NULL. " +
                "The shim exposes no error handle for failed opens, " +
                "so no further native diagnostic is available.");
        }

        return new MinigrafPreparedDatabase(new Native.SafeMgsDatabaseHandle(raw));
    }

    /// <summary>
    /// Opens an in-memory database with typed options (M3.2c; ADR 0003).
    /// WAL/page-cache-related options are documented engine no-ops in memory.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">An option value is invalid.</exception>
    /// <exception cref="MinigrafNativeLoadException">The shim could not be
    /// loaded or verified.</exception>
    /// <exception cref="MinigrafOpenException">The native open failed; when the
    /// engine reported structured information it is on
    /// <see cref="MinigrafException.NativeCategory"/> and
    /// <see cref="MinigrafException.NativeCode"/>.</exception>
    public static MinigrafPreparedDatabase OpenMemory(MinigrafOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Native.ShimNativeLoader.EnsureLoadedAndVerified();
        nint raw = Interop.ShimNativeMethods.MgsOpenInMemoryWithOptions(
            options.ToJson(), out nint errorJson);
        if (raw == nint.Zero)
        {
            throw OpenFailure(errorJson, "mgs_open_in_memory_with_options returned NULL.");
        }

        return new MinigrafPreparedDatabase(new Native.SafeMgsDatabaseHandle(raw));
    }

    /// <summary>
    /// Opens a file-backed database with typed options (M3.2c; ADR 0003).
    /// Default-open behavior is unchanged: <see cref="Open(string)"/> keeps
    /// using the engine defaults.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or
    /// <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The path is empty or an option
    /// value is invalid.</exception>
    /// <exception cref="MinigrafNativeLoadException">The shim could not be
    /// loaded or verified.</exception>
    /// <exception cref="MinigrafOpenException">The native open failed; when the
    /// engine reported structured information it is on
    /// <see cref="MinigrafException.NativeCategory"/> and
    /// <see cref="MinigrafException.NativeCode"/>.</exception>
    public static MinigrafPreparedDatabase Open(string path, MinigrafOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (path.Length == 0)
        {
            throw new ArgumentException("Database path must not be empty.", nameof(path));
        }

        options.Validate();
        Native.ShimNativeLoader.EnsureLoadedAndVerified();
        nint raw = Interop.ShimNativeMethods.MgsOpenWithOptions(
            path, options.ToJson(), out nint errorJson);
        if (raw == nint.Zero)
        {
            throw OpenFailure(
                errorJson,
                $"mgs_open_with_options returned NULL for '{path}'. " +
                "The shim exposes no database handle for failed opens.");
        }

        return new MinigrafPreparedDatabase(new Native.SafeMgsDatabaseHandle(raw));
    }

    private static MinigrafOpenException OpenFailure(nint errorJson, string fallback)
    {
        // Structured-error out-parameter (no database handle exists on open
        // failure; ADR 0003 amendment 3). Consumes/frees the owned JSON.
        Interop.NativeErrorInfo? info = Interop.NativeErrorJson.TryRead(errorJson);
        var error = new MinigrafOpenException(info?.Message ?? fallback);
        if (info is not null)
        {
            error.SetNativeError(info.Value.Category, info.Value.Code);
        }

        return error;
    }

    /// <summary>
    /// Opens a file-backed Minigraf database engine using the owned shim,
    /// creating the file when it does not exist. The path is passed as
    /// supplied (no normalization) using UTF-8.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="MinigrafNativeLoadException">The shim could not be
    /// loaded or its identity token did not match the pinned provenance.</exception>
    /// <exception cref="MinigrafOpenException">The shim open returned NULL.</exception>
    public static MinigrafPreparedDatabase Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            throw new ArgumentException("Database path must not be empty.", nameof(path));
        }

        Native.ShimNativeLoader.EnsureLoadedAndVerified();
        nint raw = Interop.ShimNativeMethods.MgsOpen(path);
        if (raw == nint.Zero)
        {
            // No handle exists on failure, so the shim diagnostic cannot be
            // consulted; the requested path is the one diagnostic available.
            throw new MinigrafOpenException(
                $"mgs_open returned NULL for '{path}'. " +
                "The shim exposes no error handle for failed opens, " +
                "so no further native diagnostic is available.");
        }

        return new MinigrafPreparedDatabase(new Native.SafeMgsDatabaseHandle(raw));
    }

    /// <summary>
    /// Prepares one query Datalog template over the shim. The returned query
    /// is registered with this database and cascade-closed by its disposal.
    /// Note prepare accepts query templates only; <c>transact</c>,
    /// <c>retract</c> and rule statements are rejected here.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="datalog"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="datalog"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="MinigrafExecutionException">The engine rejected the template.</exception>
    /// <exception cref="MinigrafResultException">The shim's slot-name JSON is malformed.</exception>
    public MinigrafPreparedQuery Prepare(string datalog)
    {
        ArgumentNullException.ThrowIfNull(datalog);
        if (datalog.Length == 0)
        {
            throw new ArgumentException("Query text must not be empty.", nameof(datalog));
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            Native.SafeMgsDatabaseHandle handle = _handle!;

            nint native = Interop.ShimNativeMethods.MgsPrepare(handle, datalog);
            if (native == nint.Zero)
            {
                throw CaptureDatabaseError(handle);
            }

            try
            {
                string[] slotNames = ReadSlotNames(native);
                var query = new MinigrafPreparedQuery(this, slotNames, native);
                _children.Add(query);
                return query;
            }
            catch
            {
                // Failed construction never leaks native state.
                Interop.ShimNativeMethods.MgsPreparedQueryFree(native);
                throw;
            }
        }
    }

    /// <summary>
    /// Executes one Datalog statement and returns the raw native JSON result
    /// as a managed string, byte-identical in content (no normalization).
    /// This is the shim's data path: prepared queries accept only query
    /// templates, so facts enter through the same engine's transact path.
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
            Native.SafeMgsDatabaseHandle handle = _handle!;

            nint result = Interop.ShimNativeMethods.MgsExecute(handle, datalog);
            if (result == nint.Zero)
            {
                throw CaptureDatabaseError(handle);
            }

            try
            {
                return Interop.NativeUtf8.ReadCString(result)
                    ?? throw new MinigrafExecutionException(
                        "mgs_execute returned a null string.");
            }
            finally
            {
                Interop.ShimNativeMethods.MgsStringFree(result);
            }
        }
    }

    /// <summary>
    /// Executes one Datalog statement and returns the typed managed result.
    /// Implemented as <see cref="ExecuteRaw"/> plus the existing managed
    /// JSON decoding; there is no second decoding path.
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
    /// Begins an explicit write transaction over the owned shim (M3.2b;
    /// ADR 0003). The engine holds its write mutex for the transaction's
    /// lifetime and guards writers with a thread-local flag: at most one
    /// explicit transaction may be open per thread (across all databases),
    /// and only one per database. A conflicting attempt fails
    /// deterministically instead of blocking.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="InvalidOperationException">A write transaction is
    /// already open on this thread or on this database.</exception>
    /// <exception cref="MinigrafExecutionException">The native transaction
    /// could not be created.</exception>
    public MinigrafWriteTransaction BeginWrite()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MinigrafPreparedDatabase));
            }

            if (MinigrafWriteTransaction.ActiveOnCurrentThread is not null)
            {
                throw new InvalidOperationException(
                    "A write transaction is already open on this thread; the core guards " +
                    "explicit transactions with a thread-local flag (measured semantics).");
            }

            MinigrafWriteTransaction? existing = _writeTransaction;
            if (existing is not null && !existing.IsTerminal)
            {
                throw new InvalidOperationException(
                    "A write transaction is already open on this database; " +
                    "the engine serializes writers per database.");
            }

            nint raw = Interop.ShimNativeMethods.MgsBeginWrite(_handle!);
            if (raw == nint.Zero)
            {
                throw CaptureDatabaseExecutionError("mgs_begin_write returned NULL.");
            }

            var transaction = new MinigrafWriteTransaction(this, raw);
            _writeTransaction = transaction;
            MinigrafWriteTransaction.MarkActive(transaction);
            return transaction;
        }
    }

    /// <summary>
    /// Releases the native database, rolling back any open write transaction
    /// and cascade-closing registered open queries first. Idempotent. Waits
    /// for any in-flight call on this database rather than racing it.
    /// </summary>
    /// <exception cref="InvalidOperationException">An open write transaction
    /// exists that was begun on another thread; terminalize it on its owning
    /// thread first (thread-affine transactions are never torn down
    /// cross-thread; ADR 0003 amendment 2).</exception>
    public void Dispose()
    {
        // Terminalize an open write transaction before taking the gate: a
        // concurrent database operation may hold the gate while blocked on
        // the engine write mutex that only this terminalization can release.
        MinigrafWriteTransaction? transaction = Volatile.Read(ref _writeTransaction);
        if (transaction is not null && !transaction.IsTerminal)
        {
            transaction.RollbackForCascade();
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            // A transaction could have been begun between the check above and
            // this gate acquisition; apply the same deterministic rule.
            transaction = _writeTransaction;
            if (transaction is not null && !transaction.IsTerminal)
            {
                transaction.RollbackForCascade();
            }

            _disposed = true;
            foreach (MinigrafPreparedQuery child in _children.ToArray())
            {
                child.CloseForCascade();
            }

            _children.Clear();
            _handle?.Dispose();
            _handle = null;
        }
    }

    internal bool IsDisposed => _disposed;

    /// <summary>Unregisters a closed child. The gate must be held.</summary>
    internal void Unregister(MinigrafPreparedQuery query)
    {
        _children.Remove(query);
    }

    /// <summary>Unregisters a terminal write transaction.</summary>
    internal void UnregisterWriteTransaction(MinigrafWriteTransaction transaction)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_writeTransaction, transaction))
            {
                _writeTransaction = null;
            }
        }
    }

    private MinigrafExecutionException CaptureDatabaseExecutionError(string fallback)
    {
        Interop.NativeErrorInfo? info = Interop.NativeErrorJson.TryRead(
            Interop.ShimNativeMethods.MgsLastErrorJson(_handle!));
        string message = info?.Message ?? fallback;
        var error = new MinigrafExecutionException(message);
        if (info is not null)
        {
            error.SetNativeError(info.Value.Category, info.Value.Code);
        }

        return error;
    }

    private string[] ReadSlotNames(nint native)
    {
        nint json = Interop.ShimNativeMethods.MgsPreparedSlotNames(native);
        if (json == nint.Zero)
        {
            throw CapturePreparedError(native);
        }

        try
        {
            string text = Interop.NativeUtf8.ReadCString(json)
                ?? throw new MinigrafResultException(
                    "mgs_prepared_slot_names returned a null string.");
            try
            {
                // AOT/trim-safe manual parse: a plain string array is all
                // the shim can emit; anything else is a result-contract
                // failure, not a runtime-configuration problem.
                using var document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    throw new MinigrafResultException(
                        "mgs_prepared_slot_names returned a non-array shape.");
                }

                var names = new List<string>(root.GetArrayLength());
                foreach (JsonElement element in root.EnumerateArray())
                {
                    names.Add(element.ValueKind == JsonValueKind.String
                        ? element.GetString() ?? throw new MinigrafResultException(
                            "mgs_prepared_slot_names returned a null slot name.")
                        : throw new MinigrafResultException(
                            "mgs_prepared_slot_names returned a non-string slot name."));
                }

                return [.. names];
            }
            catch (JsonException ex)
            {
                throw new MinigrafResultException(
                    "mgs_prepared_slot_names returned malformed JSON.", ex);
            }
        }
        finally
        {
            Interop.ShimNativeMethods.MgsStringFree(json);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private static MinigrafExecutionException CaptureDatabaseError(
        Native.SafeMgsDatabaseHandle handle)
    {
        // Borrowed pointer: copied before any other call on this handle can
        // replace it. The per-database gate keeps the pair atomic.
        nint errorPtr = Interop.ShimNativeMethods.MgsLastError(handle);
        if (Interop.NativeUtf8.ReadCString(errorPtr) is not { } message ||
            message.Length == 0)
        {
            message = "Minigraf execution failed without a native diagnostic.";
        }

        return new MinigrafExecutionException(message);
    }

    private static MinigrafExecutionException CapturePreparedError(nint native)
    {
        nint errorPtr = Interop.ShimNativeMethods.MgsPreparedLastError(native);
        if (Interop.NativeUtf8.ReadCString(errorPtr) is not { } message ||
            message.Length == 0)
        {
            message = "Minigraf prepared execution failed without a native diagnostic.";
        }

        return new MinigrafExecutionException(message);
    }
}
