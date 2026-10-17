namespace MinigrafSharp;

/// <summary>
/// Owns exactly one native shim <c>MgsPreparedQuery*</c>, prepared by its
/// parent <see cref="MinigrafPreparedDatabase"/>. The pointer is freed
/// exactly once under the parent's per-database gate: explicitly by
/// <see cref="Dispose"/>, or by the parent's cascade disposal when this
/// query is still open (there is no finalizer and no SafeHandle on this
/// pointer by design, ADR 0002). After either, every use throws
/// <see cref="ObjectDisposedException"/> deterministically.
/// </summary>
public sealed class MinigrafPreparedQuery : IDisposable
{
    private readonly MinigrafPreparedDatabase _parent;
    private readonly string[] _slotNames;
    private nint _native;
    private bool _disposed;

    internal MinigrafPreparedQuery(
        MinigrafPreparedDatabase parent,
        string[] slotNames,
        nint native)
    {
        _parent = parent;
        _slotNames = slotNames;
        _native = native;
    }

    /// <summary>
    /// The prepared query's bind slot names, as captured (sorted,
    /// deduplicated) at prepare time and stable for the query lifetime.
    /// </summary>
    public IReadOnlyList<string> SlotNames => _slotNames;

    /// <summary>
    /// Executes the prepared query with bindings from the dictionary.
    /// Bindings not referenced by the query are ignored (upstream
    /// semantics); a missing required binding rejects the call with
    /// <see cref="MinigrafExecutionException"/>. Results decode through the
    /// same <see cref="MinigrafResultDecoder"/> contract as raw execution.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="bindings"/>, or a binding name, is null.</exception>
    /// <exception cref="ArgumentException">A binding name is empty.</exception>
    /// <exception cref="ObjectDisposedException">The query or its parent
    /// database is disposed.</exception>
    /// <exception cref="MinigrafExecutionException">The engine rejected the
    /// execution or a binding (missing slot, type mismatch).</exception>
    /// <exception cref="MinigrafResultException">The native result has an
    /// unsupported shape.</exception>
    public MinigrafResult Execute(
        IReadOnlyDictionary<string, MinigrafBindValue> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        foreach ((string name, _) in bindings)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.Length == 0)
            {
                throw new ArgumentException(
                    "Binding names must not be empty.", nameof(bindings));
            }
        }

        // Serialize before taking the gate: pure managed work stays off the
        // per-database gate, and the closed-kind serializer throws
        // deterministically before any native call.
        string bindingsJson = MinigrafBindValue.SerializeBindings(
            bindings, nameof(bindings));

        lock (_parent._sync)
        {
            ThrowIfDisposed();
            nint native = _native;

            nint result = Interop.ShimNativeMethods.MgsPreparedExecute(
                native, bindingsJson);
            if (result == nint.Zero)
            {
                throw CapturePreparedError(native);
            }

            try
            {
                string json = Interop.NativeUtf8.ReadCString(result)
                    ?? throw new MinigrafExecutionException(
                        "mgs_prepared_execute returned a null string.");
                return MinigrafResultDecoder.Decode(json);
            }
            finally
            {
                Interop.ShimNativeMethods.MgsStringFree(result);
            }
        }
    }

    /// <summary>
    /// Releases the native prepared query exactly once. When the parent
    /// database was disposed first, the cascade already released the native
    /// state and this dispose is a harmless managed-only no-op.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_parent._sync)
        {
            Invalidate();
            _parent.Unregister(this);
        }
    }

    /// <summary>
    /// Cascade path: frees the native query under the parent's held gate.
    /// Called by the parent only while the parent gate is held.
    /// </summary>
    internal void CloseForCascade()
    {
        Invalidate();
    }

    private void Invalidate()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_native != nint.Zero)
        {
            Interop.ShimNativeMethods.MgsPreparedQueryFree(_native);
            _native = nint.Zero;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            // The parent disposed while this query was still open (cascade)
            // and an explicit dispose produce the same deterministic state.
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private MinigrafExecutionException CapturePreparedError(nint native)
    {
        // Structured first (M3.2d): consumes/frees the owned JSON. The
        // borrowed legacy text is only a fallback for an absent structured
        // state, which degrades to Unknown classification.
        Interop.NativeErrorInfo? info = Interop.NativeErrorJson.TryRead(
            Interop.ShimNativeMethods.MgsPreparedLastErrorJson(native));
        if (info is not null)
        {
            var structured = new MinigrafExecutionException(info.Value.Message);
            structured.SetNativeError(info.Value.Category, info.Value.Code);
            return structured;
        }

        nint errorPtr = Interop.ShimNativeMethods.MgsPreparedLastError(native);
        string? message = Interop.NativeUtf8.ReadCString(errorPtr);
        if (string.IsNullOrEmpty(message))
        {
            message = "Minigraf prepared execution failed without a native diagnostic.";
        }

        return new MinigrafExecutionException(message);
    }
}
