namespace MinigrafSharp;

/// <summary>
/// The scalar kinds that survive the result JSON boundary. On the owned-shim
/// surface (<see cref="MinigrafPreparedDatabase"/> and its prepared/transaction
/// paths) native <c>Ref</c> and <c>Keyword</c> values are typed
/// (<see cref="Ref"/>/<see cref="Keyword"/>). The official
/// <see cref="MinigrafDatabase"/> path keeps the measured minigraf-c behavior:
/// refs and keywords arrive as flattened <see cref="String"/> values. Native
/// non-finite floats arrive as JSON null and are therefore <see cref="Null"/>.
/// </summary>
public enum MinigrafValueKind
{
    /// <summary>No value (including native NaN and infinities, which the C
    /// binding serializes as JSON null).</summary>
    Null,

    /// <summary>UTF-8 text (including flattened refs/keywords from the official
    /// minigraf-c surface).</summary>
    String,

    /// <summary>Signed 64-bit integer (native <c>Integer(i64)</c>).</summary>
    Integer,

    /// <summary>64-bit float (native <c>Float(f64)</c>).</summary>
    Float,

    /// <summary>Boolean.</summary>
    Boolean,

    /// <summary>Entity reference (native <c>Ref(Uuid)</c>) on the owned-shim
    /// surface; access with <see cref="MinigrafValue.GetRef"/>.</summary>
    Ref,

    /// <summary>Keyword (native <c>Keyword(String)</c>) on the owned-shim
    /// surface; access with <see cref="MinigrafValue.GetKeyword"/>.</summary>
    Keyword,
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
    private readonly Guid _reference;

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
        _reference = Guid.Empty;
    }

    internal MinigrafValue(long value)
    {
        Kind = MinigrafValueKind.Integer;
        _string = null;
        _integer = value;
        _float = 0;
        _boolean = false;
        _reference = Guid.Empty;
    }

    internal MinigrafValue(double value)
    {
        Kind = MinigrafValueKind.Float;
        _string = null;
        _integer = 0;
        _float = value;
        _boolean = false;
        _reference = Guid.Empty;
    }

    internal MinigrafValue(bool value)
    {
        Kind = MinigrafValueKind.Boolean;
        _string = null;
        _integer = 0;
        _float = 0;
        _boolean = value;
        _reference = Guid.Empty;
    }

    private MinigrafValue(MinigrafValueKind kind, string? text, Guid reference)
    {
        Kind = kind;
        _string = text;
        _integer = 0;
        _float = 0;
        _boolean = false;
        _reference = reference;
    }

    /// <summary>Creates a typed <see cref="MinigrafValueKind.Ref"/> value
    /// (owned-shim surface only).</summary>
    internal static MinigrafValue RefValue(Guid value) =>
        new(MinigrafValueKind.Ref, null, value);

    /// <summary>Creates a typed <see cref="MinigrafValueKind.Keyword"/> value
    /// (owned-shim surface only).</summary>
    internal static MinigrafValue KeywordValue(string value) =>
        new(MinigrafValueKind.Keyword, value, Guid.Empty);

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

    /// <summary>
    /// Returns the entity reference as a <see cref="Guid"/>. Throws if the
    /// kind is not <see cref="MinigrafValueKind.Ref"/>.
    /// </summary>
    public Guid GetRef() =>
        Kind == MinigrafValueKind.Ref
            ? _reference
            : throw WrongKind(nameof(GetRef), MinigrafValueKind.Ref);

    /// <summary>
    /// Returns the keyword exactly as stored (colon-prefixed, case-sensitive,
    /// no normalization). Throws if the kind is not
    /// <see cref="MinigrafValueKind.Keyword"/>.
    /// </summary>
    public string GetKeyword() =>
        Kind == MinigrafValueKind.Keyword && _string is not null
            ? _string
            : throw WrongKind(nameof(GetKeyword), MinigrafValueKind.Keyword);

    /// <inheritdoc/>
    public bool Equals(MinigrafValue other) =>
        Kind == other.Kind && Kind switch
        {
            MinigrafValueKind.Null => true,
            MinigrafValueKind.String => _string == other._string,
            MinigrafValueKind.Integer => _integer == other._integer,
            MinigrafValueKind.Float => _float.Equals(other._float),
            MinigrafValueKind.Boolean => _boolean == other._boolean,
            MinigrafValueKind.Ref => _reference == other._reference,
            MinigrafValueKind.Keyword => _string == other._string,
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
            MinigrafValueKind.Ref => HashCode.Combine(Kind, _reference),
            MinigrafValueKind.Keyword => HashCode.Combine(Kind, _string),
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
            MinigrafValueKind.Ref => $"Ref({_reference:D})",
            MinigrafValueKind.Keyword => $"Keyword({_string})",
            _ => "Null",
        };

    private InvalidOperationException WrongKind(string accessor, MinigrafValueKind expected) =>
        new($"Minigraf value kind is {Kind}; {accessor} requires {expected}.");
}
