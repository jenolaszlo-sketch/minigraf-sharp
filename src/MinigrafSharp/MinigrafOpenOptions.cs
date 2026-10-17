using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MinigrafSharp;

/// <summary>
/// Typed open options for the owned-shim database surface
/// (<see cref="MinigrafPreparedDatabase.Open(string, MinigrafOpenOptions)"/>,
/// <see cref="MinigrafPreparedDatabase.OpenMemory(MinigrafOpenOptions)"/>).
/// Closed subset of the pinned Minigraf 2.0.7 <c>OpenOptions</c> with a
/// direct, qualified native meaning (ADR 0003):
/// <see cref="WalCheckpointThreshold"/>, <see cref="MaxDerivedFacts"/>,
/// <see cref="MaxResults"/> and <see cref="Synchronous"/>. Null properties
/// leave the engine default unchanged; there are no managed-only options.
/// </summary>
/// <remarks>
/// Options are immutable values: build a new instance with an object
/// initializer or start from <see cref="Default"/>. Invalid values
/// (undefined enum values) are rejected with <see cref="ArgumentException"/>
/// before any native call. <see cref="MinigrafPreparedDatabase.Open(string)"/>
/// and <see cref="MinigrafPreparedDatabase.OpenMemory()"/> are unchanged and
/// keep the engine defaults.
/// </remarks>
public sealed class MinigrafOpenOptions
{
    /// <summary>
    /// Value for <see cref="WalCheckpointThreshold"/> that disables every
    /// implicit checkpoint (auto-threshold and close-time), matching the
    /// engine's <c>usize::MAX</c> sentinel on the qualified 64-bit RIDs. Only
    /// an explicit checkpoint (or WAL replay at reopen) then persists facts.
    /// </summary>
    public const ulong Unlimited = ulong.MaxValue;

    /// <summary>Engine defaults (all options unset).</summary>
    public static MinigrafOpenOptions Default { get; } = new();

    /// <summary>
    /// WAL entries committed before an automatic checkpoint; engine default
    /// 1000. <see cref="Unlimited"/> disables implicit checkpoints. No effect
    /// on in-memory databases.
    /// </summary>
    public ulong? WalCheckpointThreshold { get; init; }

    /// <summary>Maximum facts derived per recursive rule iteration; engine
    /// default 1_000_000.</summary>
    public ulong? MaxDerivedFacts { get; init; }

    /// <summary>Maximum facts a rule query may derive; engine default
    /// 1_000_000. The database's own facts do not count against it.</summary>
    public ulong? MaxResults { get; init; }

    /// <summary>WAL durability mode; engine default
    /// <see cref="MinigrafSyncMode.Full"/>. No effect on in-memory
    /// databases.</summary>
    public MinigrafSyncMode? Synchronous { get; init; }

    internal void Validate()
    {
        if (Synchronous is { } mode &&
            mode is not (MinigrafSyncMode.Full or MinigrafSyncMode.Normal))
        {
            throw new ArgumentException(
                $"Undefined {nameof(MinigrafSyncMode)} value {(int)mode}; " +
                "only Full and Normal are supported.",
                nameof(Synchronous));
        }
    }

    /// <summary>Serializes the closed schema; unset options are omitted.</summary>
    internal string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (WalCheckpointThreshold is { } threshold)
            {
                writer.WriteNumber("wal_checkpoint_threshold", threshold);
            }

            if (MaxDerivedFacts is { } maxDerived)
            {
                writer.WriteNumber("max_derived_facts", maxDerived);
            }

            if (MaxResults is { } maxResults)
            {
                writer.WriteNumber("max_results", maxResults);
            }

            if (Synchronous is { } mode)
            {
                writer.WriteString("synchronous", mode == MinigrafSyncMode.Full ? "full" : "normal");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
