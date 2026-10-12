using System.Text.Json;

namespace MinigrafSharp;

/// <summary>
/// Decodes the raw JSON produced by <c>minigraf_execute</c> into the managed
/// result model. Pure managed code over an already-freed native string: it
/// knows nothing about native handles or interop. Strict and closed: exactly
/// the four shapes emitted by minigraf-c v2.0.5 decode, everything else
/// fails with <see cref="MinigrafResultException"/>. Unknown extra
/// properties are ignored only when the known shape is unambiguous and
/// valid; conflicting result semantics always fail.
/// </summary>
internal static class MinigrafResultDecoder
{
    internal static MinigrafResult Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new MinigrafResultException(
                "Native result is not valid JSON.", ex);
        }

        using (document)
        {
            return DecodeRoot(document.RootElement);
        }
    }

    private static MinigrafResult DecodeRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new MinigrafResultException(
                "Native result must be a JSON object.");
        }

        bool hasTransacted = root.TryGetProperty("transacted", out JsonElement transacted);
        bool hasRetracted = root.TryGetProperty("retracted", out JsonElement retracted);
        bool hasOk = root.TryGetProperty("ok", out JsonElement ok);
        bool hasVariables = root.TryGetProperty("variables", out JsonElement variables);
        bool hasResults = root.TryGetProperty("results", out JsonElement results);

        if (hasVariables != hasResults)
        {
            throw new MinigrafResultException(
                "Native query result requires both 'variables' and 'results'.");
        }

        int shapes = (hasTransacted ? 1 : 0)
            + (hasRetracted ? 1 : 0)
            + (hasOk ? 1 : 0)
            + ((hasVariables && hasResults) ? 1 : 0);
        if (shapes != 1)
        {
            throw new MinigrafResultException(
                "Native result has no unambiguous known shape " +
                "(exactly one of transacted, retracted, ok, or variables+results is required).");
        }

        if (hasTransacted)
        {
            return new MinigrafTransactionResult(DecodeTransactionId(transacted, "transacted"));
        }

        if (hasRetracted)
        {
            return new MinigrafRetractionResult(DecodeTransactionId(retracted, "retracted"));
        }

        if (hasOk)
        {
            if (ok.ValueKind != JsonValueKind.True)
            {
                throw new MinigrafResultException(
                    "Native ok result must be exactly {\"ok\":true}.");
            }

            return MinigrafOkResult.Instance;
        }

        return DecodeQuery(variables, results);
    }

    private static ulong DecodeTransactionId(JsonElement element, string field)
    {
        // Upstream TxId is u64 and always serialized as a plain JSON integer.
        if (element.ValueKind != JsonValueKind.Number
            || !IsPlainInteger(element.GetRawText())
            || !element.TryGetUInt64(out ulong id))
        {
            throw new MinigrafResultException(
                $"Native '{field}' must be an unsigned transaction id.");
        }

        return id;
    }

    private static MinigrafQueryResult DecodeQuery(JsonElement variables, JsonElement results)
    {
        if (variables.ValueKind != JsonValueKind.Array)
        {
            throw new MinigrafResultException(
                "Native query result 'variables' must be an array.");
        }

        if (results.ValueKind != JsonValueKind.Array)
        {
            throw new MinigrafResultException(
                "Native query result 'results' must be an array.");
        }

        var names = new List<string>();
        foreach (JsonElement variable in variables.EnumerateArray())
        {
            if (variable.ValueKind != JsonValueKind.String)
            {
                throw new MinigrafResultException(
                    "Native query variable names must be strings.");
            }

            names.Add(ReadString(variable));
        }

        var rows = new List<MinigrafRow>();
        foreach (JsonElement row in results.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                throw new MinigrafResultException(
                    "Native query rows must be arrays.");
            }

            var values = new List<MinigrafValue>();
            foreach (JsonElement cell in row.EnumerateArray())
            {
                values.Add(DecodeValue(cell));
            }

            rows.Add(new MinigrafRow([.. names], [.. values]));
        }

        // ReadOnlyCollection snapshots: consumers observe IReadOnlyList but
        // cannot reach a mutable List<T> or array behind it.
        return new MinigrafQueryResult(
            new System.Collections.ObjectModel.ReadOnlyCollection<string>([.. names]),
            new System.Collections.ObjectModel.ReadOnlyCollection<MinigrafRow>([.. rows]));
    }

    private static MinigrafValue DecodeValue(JsonElement cell) =>
        cell.ValueKind switch
        {
            JsonValueKind.String => new MinigrafValue(ReadString(cell)),
            JsonValueKind.Number => DecodeNumber(cell),
            JsonValueKind.True => new MinigrafValue(true),
            JsonValueKind.False => new MinigrafValue(false),
            JsonValueKind.Null => MinigrafValue.Null,
            _ => throw new MinigrafResultException(
                "Native query values must be JSON scalars."),
        };

    // The result boundary stays closed: System.Text.Json's reader can throw
    // InvalidOperationException for string content it cannot transcode (for
    // example an unpaired UTF-16 surrogate escape). Translate any such reader
    // failure into MinigrafResultException rather than leaking a non-Minigraf
    // exception to callers of Execute.
    private static string ReadString(JsonElement element)
    {
        string? value;
        try
        {
            value = element.GetString();
        }
        catch (InvalidOperationException ex)
        {
            throw new MinigrafResultException(
                "Native result contains a string value that cannot be read.", ex);
        }

        return value ?? throw new MinigrafResultException(
            "Native result contains a missing string value.");
    }

    private static MinigrafValue DecodeNumber(JsonElement cell)
    {
        string raw = cell.GetRawText();
        if (IsPlainInteger(raw))
        {
            // Upstream integers are i64; anything plain-integer but out of
            // range is a malformed result, not a float.
            if (cell.TryGetInt64(out long integer))
            {
                return new MinigrafValue(integer);
            }

            throw new MinigrafResultException(
                "Native integer value is out of signed 64-bit range.");
        }

        if (cell.TryGetDouble(out double floating))
        {
            return new MinigrafValue(floating);
        }

        throw new MinigrafResultException(
            "Native float value is not a finite JSON number.");
    }

    private static bool IsPlainInteger(string raw)
    {
        if (raw.Length == 0)
        {
            return false;
        }

        int start = raw[0] == '-' ? 1 : 0;
        if (start == raw.Length)
        {
            return false;
        }

        for (int i = start; i < raw.Length; i++)
        {
            if (raw[i] < '0' || raw[i] > '9')
            {
                return false;
            }
        }

        return true;
    }
}
