namespace MinigrafSharp;

/// <summary>
/// WAL durability mode for file-backed owned-shim databases
/// (<see cref="MinigrafOpenOptions.Synchronous"/>). Mirrors the pinned
/// Minigraf 2.0.7 <c>SyncMode</c>.
/// </summary>
public enum MinigrafSyncMode
{
    /// <summary>Flush the WAL after every write (engine default; committed
    /// writes are durable immediately).</summary>
    Full = 0,

    /// <summary>No per-write flush; the WAL entry is still written (safe
    /// across ordinary process crashes) but only forced to disk by a
    /// checkpoint. Intended for bulk loaders. No effect on in-memory
    /// databases.</summary>
    Normal = 1,
}
