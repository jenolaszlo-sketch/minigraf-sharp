namespace MinigrafSharp;

/// <summary>
/// The scalar kinds that survive the <c>minigraf-c</c> JSON boundary.
/// Native <c>Ref</c> and <c>Keyword</c> values arrive as JSON strings and
/// are therefore <see cref="String"/> here; native non-finite floats arrive
/// as JSON null and are therefore <see cref="Null"/> here.
/// </summary>
public enum MinigrafValueKind
{
    /// <summary>No value (including native NaN and infinities, which the C
    /// binding serializes as JSON null).</summary>
    Null,

    /// <summary>UTF-8 text (including flattened native refs and keywords).</summary>
    String,

    /// <summary>Signed 64-bit integer (native <c>Integer(i64)</c>).</summary>
    Integer,

    /// <summary>64-bit float (native <c>Float(f64)</c>).</summary>
    Float,

    /// <summary>Boolean.</summary>
    Boolean,
}

/// <summary>
/// One immutable scalar query value. Type-sensitive: accessors for the
/// wrong kind throw <see cref="InvalidOperationException"/>; no silent
/// coercion occurs. Equality is kind-sensitive: <c>Integer(42)</c> does not
/// equal <c>Float(42.0)</c>.
/// </summary>
public readonly struct MinigrafValue : IEquatable<MinigrafValue>
{
    private readonly string? _string;
    private readonly long _integer;
    private readonly double _float;
    private readonly bool _boolean;

    /// <summary>
    /// The kind of this value.
    /// </summary>
    public MinigrafValueKind Kind { get; }

    /// <summary>
    /// True when <see cref="Kind"/> is <see cref="MinigrafValueKind.Null"/>.
    /// </summary>
    public bool IsNull => Kind == MinigrafValueKind.Null;

    /// <summary>
    /// The null value. <c>default(MinigrafValue)</c> is also null.
    /// </summary>
    public static MinigrafValue Null => default;

    internal MinigrafValue(string value)
    {
        Kind = MinigrafValueKind.String;
        _string = value;
        _integer = 0;
        _float = 0;
        _boolean = false;
    }

    internal MinigrafValue(long value)
    {
        Kind = MinigrafValueKind.Integer;
        _string = null;
        _integer = value;
        _float = 0;
        _boolean = false;
    }

    internal MinigrafValue(double value)
    {
        Kind = MinigrafValueKind.Float;
        _string = null;
        _integer = 0;
        _float = value;
        _boolean = false;
    }

    internal MinigrafValue(bool value)
    {
        Kind = MinigrafValueKind.Boolean;
        _string = null;
        _integer = 0;
        _float = 0;
        _boolean = value;
    }

    /// <summary>
    /// Returns the string value. Throws if the kind is not
    /// <see cref="MinigrafValueKind.String"/>.
    /// </summary>
    public string GetString() =>
        Kind == MinigrafValueKind.String && _string is not null
            ? _string
            : throw WrongKind(nameof(GetString), MinigrafValueKind.String);

    /// <summary>
    /// Returns the integer value. Throws if the kind is not
    /// <see cref="MinigrafValueKind.Integer"/>.
    /// </summary>
    public long GetInt64() =>
        Kind == MinigrafValueKind.Integer
            ? _integer
            : throw WrongKind(nameof(GetInt64), MinigrafValueKind.Integer);

    /// <summary>
    /// Returns the float value. Throws if the kind is not
    /// <see cref="MinigrafValueKind.Float"/>.
    /// </summary>
    public double GetDouble() =>
        Kind == MinigrafValueKind.Float
            ? _float
            : throw WrongKind(nameof(GetDouble), MinigrafValueKind.Float);

    /// <summary>
    /// Returns the Boolean value. Throws if the kind is not
    /// <see cref="MinigrafValueKind.Boolean"/>.
    /// </summary>
    public bool GetBoolean() =>
        Kind == MinigrafValueKind.Boolean
            ? _boolean
            : throw WrongKind(nameof(GetBoolean), MinigrafValueKind.Boolean);

    /// <inheritdoc/>
    public bool Equals(MinigrafValue other) =>
        Kind == other.Kind && Kind switch
        {
            MinigrafValueKind.Null => true,
            MinigrafValueKind.String => _string == other._string,
            MinigrafValueKind.Integer => _integer == other._integer,
            MinigrafValueKind.Float => _float.Equals(other._float),
            MinigrafValueKind.Boolean => _boolean == other._boolean,
            _ => false,
        };

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is MinigrafValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        Kind switch
        {
            MinigrafValueKind.String => HashCode.Combine(Kind, _string),
            MinigrafValueKind.Integer => HashCode.Combine(Kind, _integer),
            MinigrafValueKind.Float => HashCode.Combine(Kind, _float),
            MinigrafValueKind.Boolean => HashCode.Combine(Kind, _boolean),
            _ => HashCode.Combine(Kind, 0),
        };

    /// <summary>
    /// Kind-sensitive equality.
    /// </summary>
    public static bool operator ==(MinigrafValue left, MinigrafValue right) =>
        left.Equals(right);

    /// <summary>
    /// Kind-sensitive inequality.
    /// </summary>
    public static bool operator !=(MinigrafValue left, MinigrafValue right) =>
        !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        Kind switch
        {
            MinigrafValueKind.String => $"String({_string})",
            MinigrafValueKind.Integer => $"Integer({_integer})",
            MinigrafValueKind.Float => $"Float({_float})",
            MinigrafValueKind.Boolean => $"Boolean({_boolean})",
            _ => "Null",
        };

    private InvalidOperationException WrongKind(string accessor, MinigrafValueKind expected) =>
        new($"Minigraf value kind is {Kind}; {accessor} requires {expected}.");
}
