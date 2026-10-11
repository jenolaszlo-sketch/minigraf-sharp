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
/// A native database open failed. minigraf-c v2.0.5 exposes no error handle
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
