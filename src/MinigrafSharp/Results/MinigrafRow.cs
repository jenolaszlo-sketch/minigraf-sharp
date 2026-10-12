namespace MinigrafSharp;

/// <summary>
/// One immutable query result row, aligned with its query's variable list.
/// Positional and variable-name access return the same values. A missing
/// variable name is not a database null: the string indexer throws
/// <see cref="KeyNotFoundException"/> and <see cref="TryGetValue"/>
/// returns false.
/// </summary>
public sealed class MinigrafRow
{
    private readonly MinigrafValue[] _values;
    private readonly Dictionary<string, int> _index;

    internal MinigrafRow(string[] variables, MinigrafValue[] values)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(values);
        if (variables.Length != values.Length)
        {
            throw new MinigrafResultException(
                $"Row width {values.Length} does not match variable count {variables.Length}.");
        }

        var index = new Dictionary<string, int>(variables.Length, StringComparer.Ordinal);
        for (int i = 0; i < variables.Length; i++)
        {
            string variable = variables[i] ?? throw new MinigrafResultException(
                "Query variable names must be strings.");
            if (!index.TryAdd(variable, i))
            {
                throw new MinigrafResultException(
                    $"Duplicate query variable '{variable}'.");
            }
        }

        _values = values;
        _index = index;
    }

    /// <summary>
    /// The number of values in this row (always equal to the variable count).
    /// </summary>
    public int Count => _values.Length;

    /// <summary>
    /// Returns the value at a positional index.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public MinigrafValue this[int index] =>
        index >= 0 && index < _values.Length
            ? _values[index]
            : throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Row index {index} is out of range for {Count} values.");

    /// <summary>
    /// Returns the value for a variable name exactly as reported by the
    /// engine (including the leading <c>?</c>).
    /// </summary>
    /// <exception cref="KeyNotFoundException">The variable is not in this row.</exception>
    public MinigrafValue this[string variable]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(variable);
            return _values[_index[variable]];
        }
    }

    /// <summary>
    /// Looks up a variable name without throwing for unknown names.
    /// </summary>
    public bool TryGetValue(string variable, out MinigrafValue value)
    {
        if (variable is not null && _index.TryGetValue(variable, out int index))
        {
            value = _values[index];
            return true;
        }

        value = default;
        return false;
    }
}
