using MinigrafSharp.Interop;
using MinigrafSharp.Native;

namespace MinigrafSharp;

/// <summary>
/// An explicit write transaction over the owned shim, begun by
/// <see cref="MinigrafPreparedDatabase.BeginWrite"/>. Writes are staged until
/// <see cref="Commit"/>; reads inside the transaction see committed facts plus
/// staged facts (measured native behavior). The transaction is thread-affine:
/// every member, including <see cref="Dispose"/>, must be called from the
/// thread that began it.
/// </summary>
/// <remarks>
/// Ownership and terminal states mirror the measured core semantics (ADR 0003):
/// <see cref="Commit"/> and <see cref="Rollback"/> are terminal;
/// <see cref="Dispose"/> on a transaction that is still open rolls it back and
/// never commits implicitly. A failed <see cref="Commit"/> is also terminal
/// (the native transaction is consumed and nothing is applied); its structured
/// diagnostics are retained on the exception. There is no managed finalizer, so
/// no finalizer thread can tear down a thread-affine native transaction.
/// </remarks>
public sealed class MinigrafWriteTransaction : IDisposable
{
    // The core guards write transactions with a thread-local flag, so at most
    // one explicit write transaction can be open per thread - across ALL
    // databases (measured M3.2b characterization). Mirroring it managed-side
    // gives a deterministic exception instead of a native INT-001.
    [ThreadStatic]
    private static MinigrafWriteTransaction? t_activeOnThread;

    private readonly MinigrafPreparedDatabase _database;
    private readonly object _sync = new();
    private readonly int _ownerThreadId;
    private nint _native;
    private bool _terminal;

    internal MinigrafWriteTransaction(MinigrafPreparedDatabase database, nint native)
    {
        _database = database;
        _native = native;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    internal bool IsTerminal => _terminal;

    /// <summary>The explicit transaction open on this thread, if any.</summary>
    internal static MinigrafWriteTransaction? ActiveOnCurrentThread => t_activeOnThread;

    internal static void MarkActive(MinigrafWriteTransaction transaction) =>
        t_activeOnThread = transaction;

    /// <summary>
    /// Executes a Datalog command inside the transaction and returns the raw
    /// native result JSON (same shapes as
    /// <see cref="MinigrafPreparedDatabase.ExecuteRaw"/>). Writes are staged and
    /// acknowledge with an <c>ok</c> result; reads see staged and committed
    /// facts.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="datalog"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Called from a thread other
    /// than the one that began the transaction.</exception>
    /// <exception cref="ObjectDisposedException">The transaction is terminal or
    /// its database is disposed.</exception>
    /// <exception cref="MinigrafExecutionException">The command failed;
    /// <see cref="MinigrafException.NativeCategory"/> and
    /// <see cref="MinigrafException.NativeCode"/> carry the structured
    /// classification when available.</exception>
    public string ExecuteRaw(string datalog)
    {
        ArgumentNullException.ThrowIfNull(datalog);
        lock (_sync)
        {
            ThrowIfUnusable();
            nint result = ShimNativeMethods.MgsTxExecute(_native, datalog);
            if (result == nint.Zero)
            {
                throw CaptureExecutionError("mgs_tx_execute returned no result.");
            }

            try
            {
                return NativeUtf8.ReadCString(result)
                    ?? throw new MinigrafExecutionException(
                        "mgs_tx_execute returned a null string.");
            }
            finally
            {
                ShimNativeMethods.MgsStringFree(result);
            }
        }
    }

    /// <summary>
    /// Executes a Datalog command inside the transaction and decodes the native
    /// result through the shared <see cref="MinigrafResultDecoder"/> contract.
    /// </summary>
    public MinigrafResult Execute(string datalog)
    {
        string json = ExecuteRaw(datalog);
        return MinigrafResultDecoder.Decode(json);
    }

    /// <summary>
    /// Commits the transaction atomically. Terminal. On failure the native
    /// transaction is consumed without applying anything; this object becomes
    /// terminal and the failure's structured diagnostics are attached to the
    /// thrown <see cref="MinigrafExecutionException"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from a thread other
    /// than the one that began the transaction.</exception>
    /// <exception cref="ObjectDisposedException">The transaction is already
    /// terminal or its database is disposed.</exception>
    /// <exception cref="MinigrafExecutionException">The commit failed.</exception>
    public void Commit()
    {
        lock (_sync)
        {
            ThrowIfUnusable();
            int rc = ShimNativeMethods.MgsTxCommit(_native);
            if (rc != 0)
            {
                MinigrafExecutionException error =
                    CaptureExecutionError("mgs_tx_commit failed.");
                FinishTerminal();
                throw error;
            }

            FinishTerminal();
        }
    }

    /// <summary>
    /// Rolls the transaction back. Terminal and infallible. Staged writes are
    /// discarded and the database remains usable.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from a thread other
    /// than the one that began the transaction.</exception>
    /// <exception cref="ObjectDisposedException">The transaction is already
    /// terminal or its database is disposed.</exception>
    public void Rollback()
    {
        lock (_sync)
        {
            ThrowIfUnusable();
            ShimNativeMethods.MgsTxRollback(_native);
            FinishTerminal();
        }
    }

    /// <summary>
    /// Rolls back a still-open transaction (never commits) and releases the
    /// native wrapper. Idempotent after terminalization. Thread-affine: a
    /// cross-thread dispose of an open transaction throws instead of tearing
    /// down thread-affine native state.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_terminal)
            {
                return;
            }

            ThrowIfWrongThread("dispose");
            ShimNativeMethods.MgsTxRollback(_native);
            FinishTerminal();
        }
    }

    /// <summary>
    /// Cascade path used by the owning database's disposal. Same contract as
    /// <see cref="Dispose"/>, and only legal on the transaction's owner thread.
    /// </summary>
    internal void RollbackForCascade()
    {
        lock (_sync)
        {
            if (_terminal)
            {
                return;
            }

            ThrowIfWrongThread("database disposal");
            ShimNativeMethods.MgsTxRollback(_native);
            FinishTerminal();
        }
    }

    private void ThrowIfUnusable()
    {
        if (_terminal)
        {
            throw new ObjectDisposedException(
                nameof(MinigrafWriteTransaction),
                "The write transaction is already terminal (committed, rolled back or disposed).");
        }

        if (_database.IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(MinigrafPreparedDatabase),
                "The owning database is disposed.");
        }

        ThrowIfWrongThread("use");
    }

    private void ThrowIfWrongThread(string operation)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                $"Write transactions are thread-affine: {operation} must run on the thread " +
                "that began the transaction (measured core semantics; ADR 0003).");
        }
    }

    /// <summary>Frees the native wrapper and unregisters from the database.</summary>
    private void FinishTerminal()
    {
        _ = ShimNativeMethods.MgsWriteTxFree(_native);
        _native = nint.Zero;
        _terminal = true;
        if (ReferenceEquals(t_activeOnThread, this))
        {
            t_activeOnThread = null;
        }

        _database.UnregisterWriteTransaction(this);
    }

    private MinigrafExecutionException CaptureExecutionError(string fallback)
    {
        // The shim keeps the structured diagnostic on the wrapper after
        // commit/rollback consumed the inner transaction (ADR 0003 amendment
        // 1), so it is read before the wrapper is freed.
        NativeErrorInfo? info = NativeErrorJson.TryRead(
            ShimNativeMethods.MgsTxLastErrorJson(_native));
        string message = info?.Message ?? fallback;
        var error = new MinigrafExecutionException(message);
        if (info is not null)
        {
            error.SetNativeError(info.Value.Category, info.Value.Code);
        }

        return error;
    }
}
