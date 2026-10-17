using System.Diagnostics;
using System.Text.Json;

namespace MinigrafSharp;

/// <summary>
/// The closed set of prepared-query binding kinds, each mapping exactly one
/// shim bindings kind (M3.1a measured semantics; no reinterpretation).
/// </summary>
public enum MinigrafBindKind
{
    /// <summary><c>[$slot :attr ?v]</c> entity position; native UUID text.</summary>
    Entity,

    /// <summary>Value-position payload (<c>[?e :attr $slot]</c>, expression
    /// literals): <see cref="MinigrafValueKind.String"/>,
    /// <see cref="MinigrafValueKind.Integer"/>, <see cref="MinigrafValueKind.Float"/>,
    /// <see cref="MinigrafValueKind.Boolean"/> or <see cref="MinigrafValueKind.Null"/>.</summary>
    Val,

    /// <summary>Explicit ref identity in a value position (e.g. rule
    /// invocation arguments); native UUID text.</summary>
    ValRef,

    /// <summary>Explicit keyword identity in a value position. The text is
    /// passed through as supplied (no normalization): engine keywords are
    /// colon-prefixed, so text without the leading ':' addresses no engine
    /// keyword.</summary>
    ValKeyword,

    /// <summary><c>:as-of $slot</c> position; the engine transaction
    /// counter domain. Empty slot value: <c>:any-valid-time</c>-style
    /// unbounded instant.</summary>
    Transaction,

    /// <summary><c>:as-of $slot</c> and <c>:valid-at $slot</c> positions;
    /// unix milliseconds. Raw <c>:valid-at</c> ISO literals in the query
    /// text are not reinterpreted as equivalent to this bind kind.</summary>
    Timestamp,

    /// <summary><c>:valid-at $slot</c> unbounded (<c>:any-valid-time</c>).</summary>
    AnyValidTime,
}

/// <summary>
/// One immutable, kind-sensitive prepared-query binding payload. Created
/// only through its factory methods; accessors for wrong kinds throw
/// <see cref="InvalidOperationException"/>. Accepted payloads are total:
/// nothing escapes to the shim that it does not recognize natively, and the
/// shim rejects unknown kinds on its own (defense in depth, never relied on).
/// </summary>
public readonly struct MinigrafBindValue : IEquatable<MinigrafBindValue>
{
    private readonly Guid _entity;
    private readonly MinigrafValue _value;
    private readonly string? _keyword;
    private readonly ulong _transaction;
    private readonly long _timestamp;

    /// <summary>The kind of this binding value.</summary>
    public MinigrafBindKind Kind { get; }

    private MinigrafBindValue(
        MinigrafBindKind kind,
        Guid entity,
        MinigrafValue value,
        string? keyword,
        ulong transaction,
        long timestamp)
    {
        Kind = kind;
        _entity = entity;
        _value = value;
        _keyword = keyword;
        _transaction = transaction;
        _timestamp = timestamp;
    }

    /// <summary>Entity position binding (native UUID text).</summary>
    public static MinigrafBindValue Entity(Guid entity) =>
        new(MinigrafBindKind.Entity, entity, default, null, 0, 0);

    /// <summary>
    /// Value-position binding with one of the five payload kinds the engine
    /// accepts there (string, integer, float, boolean, null).
    /// </summary>
    /// <exception cref="ArgumentException">The payload is a non-finite
    /// float, which the bindings JSON cannot represent losslessly.</exception>
    public static MinigrafBindValue Val(MinigrafValue value)
    {
        if (value.Kind == MinigrafValueKind.Float
            && !double.IsFinite(value.GetDouble()))
        {
            throw new ArgumentException(
                "Non-finite float bind values cannot be represented in bindings JSON.",
                nameof(value));
        }

        return new MinigrafBindValue(MinigrafBindKind.Val, Guid.Empty, value, null, 0, 0);
    }

    /// <summary>Value-position ref binding (native UUID).</summary>
    public static MinigrafBindValue ValRef(Guid entity) =>
        new(MinigrafBindKind.ValRef, entity, default, null, 0, 0);

    /// <summary>
    /// Value-position keyword binding. The text is passed through as supplied,
    /// with no normalization: engine keywords are colon-prefixed.
    /// </summary>
    /// <exception cref="ArgumentNullException">The keyword text is null.</exception>
    /// <exception cref="ArgumentException">The keyword text is empty.</exception>
    public static MinigrafBindValue ValKeyword(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (keyword.Length == 0)
        {
            throw new ArgumentException("Keyword bind value must not be empty.", nameof(keyword));
        }

        return new MinigrafBindValue(MinigrafBindKind.ValKeyword, Guid.Empty, default, keyword, 0, 0);
    }

    /// <summary><c>:as-of</c> position binding on the transaction counter
    /// domain.</summary>
    public static MinigrafBindValue Transaction(ulong txCount) =>
        new(MinigrafBindKind.Transaction, Guid.Empty, default, null, txCount, 0);

    /// <summary><c>:as-of</c>/<c>:valid-at</c> position binding in unix
    /// milliseconds.</summary>
    public static MinigrafBindValue Timestamp(long unixMillis) =>
        new(MinigrafBindKind.Timestamp, Guid.Empty, default, null, 0, unixMillis);

    /// <summary>Unbounded <c>:valid-at</c> position binding.</summary>
    public static MinigrafBindValue AnyValidTime() =>
        new(MinigrafBindKind.AnyValidTime, Guid.Empty, default, null, 0, 0);

    /// <summary>The entity of an <see cref="MinigrafBindKind.Entity"/> binding.</summary>
    public Guid GetEntity() =>
        Kind == MinigrafBindKind.Entity
            ? _entity
            : throw WrongKind(nameof(GetEntity), MinigrafBindKind.Entity);

    /// <summary>The payload of an <see cref="MinigrafBindKind.Val"/> binding.</summary>
    public MinigrafValue GetValValue() =>
        Kind == MinigrafBindKind.Val
            ? _value
            : throw WrongKind(nameof(GetValValue), MinigrafBindKind.Val);

    /// <summary>The entity of a <see cref="MinigrafBindKind.ValRef"/> binding.</summary>
    public Guid GetRefValue() =>
        Kind == MinigrafBindKind.ValRef
            ? _entity
            : throw WrongKind(nameof(GetRefValue), MinigrafBindKind.ValRef);

    /// <summary>The text of a <see cref="MinigrafBindKind.ValKeyword"/> binding.</summary>
    public string GetKeywordValue() =>
        Kind == MinigrafBindKind.ValKeyword && _keyword is not null
            ? _keyword
            : throw WrongKind(nameof(GetKeywordValue), MinigrafBindKind.ValKeyword);

    /// <summary>The counter of a <see cref="MinigrafBindKind.Transaction"/> binding.</summary>
    public ulong GetTransactionValue() =>
        Kind == MinigrafBindKind.Transaction
            ? _transaction
            : throw WrongKind(nameof(GetTransactionValue), MinigrafBindKind.Transaction);

    /// <summary>The unix-milliseconds of a <see cref="MinigrafBindKind.Timestamp"/> binding.</summary>
    public long GetTimestampValue() =>
        Kind == MinigrafBindKind.Timestamp
            ? _timestamp
            : throw WrongKind(nameof(GetTimestampValue), MinigrafBindKind.Timestamp);

    /// <inheritdoc/>
    public bool Equals(MinigrafBindValue other) =>
        Kind == other.Kind && Kind switch
        {
            MinigrafBindKind.Entity => _entity == other._entity,
            MinigrafBindKind.Val => _value == other._value,
            MinigrafBindKind.ValRef => _entity == other._entity,
            MinigrafBindKind.ValKeyword => Equals(_keyword, other._keyword),
            MinigrafBindKind.Transaction => _transaction == other._transaction,
            MinigrafBindKind.Timestamp => _timestamp == other._timestamp,
            MinigrafBindKind.AnyValidTime => true,
            _ => false,
        };

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is MinigrafBindValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        Kind switch
        {
            MinigrafBindKind.Entity => HashCode.Combine(Kind, _entity),
            MinigrafBindKind.Val => HashCode.Combine(Kind, _value),
            MinigrafBindKind.ValRef => HashCode.Combine(Kind, _entity),
            MinigrafBindKind.ValKeyword => HashCode.Combine(Kind, _keyword),
            MinigrafBindKind.Transaction => HashCode.Combine(Kind, _transaction),
            MinigrafBindKind.Timestamp => HashCode.Combine(Kind, _timestamp),
            _ => HashCode.Combine(Kind, 0),
        };

    /// <summary>Kind-sensitive equality.</summary>
    public static bool operator ==(MinigrafBindValue left, MinigrafBindValue right) =>
        left.Equals(right);

    /// <summary>Kind-sensitive inequality.</summary>
    public static bool operator !=(MinigrafBindValue left, MinigrafBindValue right) =>
        !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        Kind switch
        {
            MinigrafBindKind.Entity => $"Entity({_entity.ToString("D")})",
            MinigrafBindKind.Val => $"Val({_value})",
            MinigrafBindKind.ValRef => $"ValRef({_entity.ToString("D")})",
            MinigrafBindKind.ValKeyword => $"ValKeyword({_keyword})",
            MinigrafBindKind.Transaction => $"Transaction({_transaction})",
            MinigrafBindKind.Timestamp => $"Timestamp({_timestamp})",
            _ => "AnyValidTime",
        };

    /// <summary>
    /// Serializes the bindings dictionary into the shim's bindings JSON
    /// schema (strict closed shape; each element is
    /// <c>{"name":&lt;slot name&gt;,"bind":{...}}</c> exactly as the shim
    /// accepts it, e.g. 'age' for '$age').
    /// </summary>
    internal static string SerializeBindings(
        IReadOnlyDictionary<string, MinigrafBindValue> bindings,
        string paramName)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach ((string name, MinigrafBindValue bind) in bindings)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteStartObject("bind");
                switch (bind.Kind)
                {
                    case MinigrafBindKind.Entity:
                        writer.WriteString("kind", "entity");
                        writer.WriteString("value", bind.GetEntity().ToString("D"));
                        break;
                    case MinigrafBindKind.Val:
                        writer.WriteString("kind", "val");
                        WriteValValue(writer, bind.GetValValue(), paramName);
                        break;
                    case MinigrafBindKind.ValRef:
                        writer.WriteString("kind", "ref");
                        writer.WriteString("value", bind.GetRefValue().ToString("D"));
                        break;
                    case MinigrafBindKind.ValKeyword:
                        writer.WriteString("kind", "keyword");
                        writer.WriteString("value", bind.GetKeywordValue());
                        break;
                    case MinigrafBindKind.Transaction:
                        writer.WriteString("kind", "tx_count");
                        writer.WriteNumber("value", bind.GetTransactionValue());
                        break;
                    case MinigrafBindKind.Timestamp:
                        writer.WriteString("kind", "timestamp");
                        writer.WriteNumber("value", bind.GetTimestampValue());
                        break;
                    case MinigrafBindKind.AnyValidTime:
                        writer.WriteString("kind", "any_valid_time");
                        break;
                    default:
                        // Closed over the public enum in this revision; a new
                        // kind must extend serialization in the same change.
                        throw new UnreachableException(
                            $"Unhandled bind kind {bind.Kind}.");
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteValValue(Utf8JsonWriter writer, MinigrafValue value, string paramName)
    {
        switch (value.Kind)
        {
            case MinigrafValueKind.String:
                writer.WriteString("type", "string");
                writer.WriteString("value", value.GetString());
                break;
            case MinigrafValueKind.Integer:
                writer.WriteString("type", "integer");
                writer.WriteNumber("value", value.GetInt64());
                break;
            case MinigrafValueKind.Float:
                writer.WriteString("type", "float");
                writer.WriteNumber("value", value.GetDouble());
                break;
            case MinigrafValueKind.Boolean:
                writer.WriteString("type", "boolean");
                writer.WriteBoolean("value", value.GetBoolean());
                break;
            case MinigrafValueKind.Null:
                writer.WriteString("type", "null");
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported MinigrafValue kind '{value.Kind}' in a Val bind payload.",
                    paramName);
        }
    }

    private readonly InvalidOperationException WrongKind(string accessor, MinigrafBindKind expected) =>
        new($"Minigraf bind value kind is {Kind}; {accessor} requires {expected}.");
}
