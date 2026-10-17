namespace MinigrafSharp;

/// <summary>
/// Base exception for MinigrafSharp failures. M0 carries only the failures
/// the lifecycle surface can actually produce; M1 extends the taxonomy.
/// </summary>
public class MinigrafException : Exception
{
    public MinigrafException(string message)
        : base(message)
    {
    }

    public MinigrafException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A native database open failed. minigraf-c v2.0.7 exposes no error handle
/// for failed opens, so this exception is honest about the missing detail.
/// </summary>
public sealed class MinigrafOpenException : MinigrafException
{
    public MinigrafOpenException(string message)
        : base(message)
    {
    }

    public MinigrafOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The pinned native library could not be located or loaded. The message
/// carries the RID, architecture, expected filename and probed locations.
/// </summary>
public sealed class MinigrafNativeLoadException : MinigrafException
{
    public MinigrafNativeLoadException(string message)
        : base(message)
    {
    }

    public MinigrafNativeLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A Datalog execution failed in the native engine. The message is the
/// diagnostic copied from <c>minigraf_last_error</c> at failure time, or a
/// deterministic fallback when the engine exposed no diagnostic.
/// It covers every Datalog command shape (query, transact, retract, rules):
/// it reports that execution failed, never a result category.
/// </summary>
public sealed class MinigrafExecutionException : MinigrafException
{
    public MinigrafExecutionException(string message)
        : base(message)
    {
    }

    public MinigrafExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Native execution succeeded, but the returned result cannot be
/// interpreted under the supported MinigrafSharp result contract.
/// This is a different failure boundary from
/// <see cref="MinigrafExecutionException"/>: the engine call worked, the
/// managed decoding did not. The raw JSON is deliberately not attached:
/// it may contain user database values and must not leak into logs.
/// </summary>
public sealed class MinigrafResultException : MinigrafException
{
    public MinigrafResultException(string message)
        : base(message)
    {
    }

    public MinigrafResultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A native checkpoint failed. The message is the diagnostic copied from
/// <c>minigraf_last_error</c> at failure time, or a deterministic fallback.
/// Checkpoint is storage maintenance, not Datalog execution, so this is
/// distinct from <see cref="MinigrafExecutionException"/>.
/// </summary>
public sealed class MinigrafCheckpointException : MinigrafException
{
    public MinigrafCheckpointException(string message)
        : base(message)
    {
    }

    public MinigrafCheckpointException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
