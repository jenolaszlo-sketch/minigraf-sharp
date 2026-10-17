namespace MinigrafSharp;

/// <summary>
/// Coarse category of the native (or shim-level) failure carried by
/// <see cref="MinigrafException"/>. The values mirror the pinned Minigraf
/// 2.0.7 <c>ErrorCategory</c> plus the MinigrafSharp-owned <see cref="Shim"/>
/// classification for owned-ABI misuse and <see cref="Unknown"/> for anything
/// that cannot be classified. Unknown conditions are never mapped onto a
/// known category.
/// </summary>
public enum MinigrafNativeErrorCategory
{
    /// <summary>No structured classification is available.</summary>
    Unknown = 0,

    /// <summary>Datalog/EDN syntax errors (native <c>PRS-</c> codes).</summary>
    Parser = 1,

    /// <summary>Query planning/execution errors (native <c>QRY-</c> codes).</summary>
    Query = 2,

    /// <summary>On-disk storage/file-format errors (native <c>STG-</c> codes).</summary>
    Storage = 3,

    /// <summary>Write-ahead-log errors (native <c>WAL-</c> codes).</summary>
    Wal = 4,

    /// <summary>Public API misuse or internal sequencing errors (native <c>API-</c> codes).</summary>
    Api = 5,

    /// <summary>Unclassified internal invariant violations (native <c>INT-</c> codes).</summary>
    Internal = 6,

    /// <summary>Owned-shim failures (stable <c>MGS-</c> codes, e.g. thread
    /// affinity or terminal-transaction misuse).</summary>
    Shim = 7,
}
