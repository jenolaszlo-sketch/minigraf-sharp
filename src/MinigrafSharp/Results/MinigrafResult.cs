namespace MinigrafSharp;

/// <summary>
/// Typed managed representation of one native execution result.
/// Decoded from the raw JSON produced by <c>minigraf_execute</c>; see
/// <c>docs/native-boundary.md</c> for the upstream type-flattening rules.
/// Immutable.
/// </summary>
public abstract record MinigrafResult;

/// <summary>
/// A transaction completed. The identifier is the native transaction id
/// (Unix milliseconds, upstream <c>TxId = u64</c>).
/// </summary>
public sealed record MinigrafTransactionResult(
    ulong TransactionId)
    : MinigrafResult;

/// <summary>
/// A retraction completed. The identifier is the native transaction id
/// (Unix milliseconds, upstream <c>TxId = u64</c>).
/// </summary>
public sealed record MinigrafRetractionResult(
    ulong TransactionId)
    : MinigrafResult;

/// <summary>
/// Acknowledgement for commands that produce no data (for example rule
/// definitions). There is exactly one meaningful instance.
/// </summary>
public sealed record MinigrafOkResult
    : MinigrafResult
{
    /// <summary>
    /// The acknowledgement instance.
    /// </summary>
    public static MinigrafOkResult Instance { get; } = new MinigrafOkResult();
}

/// <summary>
/// Query results: variable names in <c>:find</c> order and one row per
/// result, each row aligned with <see cref="Variables"/>. Immutable.
/// </summary>
public sealed record MinigrafQueryResult(
    IReadOnlyList<string> Variables,
    IReadOnlyList<MinigrafRow> Rows)
    : MinigrafResult;
