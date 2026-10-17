using System.Text.Json;

namespace MinigrafSharp.Interop;

/// <summary>Structured error payload copied from an owned shim JSON string.</summary>
internal readonly record struct NativeErrorInfo(
    MinigrafNativeErrorCategory Category,
    string? Code,
    string Message);

/// <summary>
/// Reads and frees the owned structured-error JSON produced by the shim
/// (<c>mgs_last_error_json</c>, <c>mgs_prepared_last_error_json</c>,
/// <c>mgs_tx_last_error_json</c>). The wire shape is closed:
/// <c>{"category","code","message"}</c>.
/// </summary>
/// <remarks>
/// Parsing is deliberately defensive and pure (no mutable state, no message
/// parsing, never throws). The approved strict fallback (ADR 0003 amendment):
/// absent or malformed metadata, non-object roots, missing/wrong-typed
/// category or message, and unknown future categories all yield
/// <see cref="MinigrafNativeErrorCategory.Unknown"/> with a null code and the
/// diagnostic text preserved. A known category with a missing or structurally
/// invalid code yields the known category with a null code. A code presented
/// with an unknown category is dropped, so a future/malformed payload cannot
/// acquire semantics.
/// </remarks>
internal static class NativeErrorJson
{
    /// <summary>
    /// Consumes the owned pointer (always frees it) and returns the parsed
    /// payload, or null when the pointer is null (no error recorded).
    /// </summary>
    internal static NativeErrorInfo? TryRead(nint ownedJson)
    {
        if (ownedJson == nint.Zero)
        {
            return null;
        }

        try
        {
            return Parse(NativeUtf8.ReadCString(ownedJson));
        }
        finally
        {
            ShimNativeMethods.MgsStringFree(ownedJson);
        }
    }

    /// <summary>
    /// Pure parser over the closed wire shape. Returns null only when there is
    /// no text at all; all malformed/untrusted shapes yield the strict
    /// <see cref="MinigrafNativeErrorCategory.Unknown"/> fallback.
    /// </summary>
    internal static NativeErrorInfo? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unknown(text);
            }

            if (!TryReadString(root, "category", out string? categoryText) ||
                !TryReadString(root, "message", out string? message) ||
                string.IsNullOrWhiteSpace(message))
            {
                return Unknown(text);
            }

            MinigrafNativeErrorCategory category = ParseCategory(categoryText);
            if (category == MinigrafNativeErrorCategory.Unknown)
            {
                // Unknown category: never expose the accompanying code.
                return new NativeErrorInfo(category, null, message);
            }

            string? code =
                TryReadString(root, "code", out string? candidate) && IsStructuredCode(candidate)
                    ? candidate
                    : null;
            return new NativeErrorInfo(category, code, message);
        }
        catch (Exception)
        {
            // A parsing problem must never replace the original operation
            // failure; keep the diagnostic text and classify as unknown.
            return Unknown(text);
        }
    }

    private static NativeErrorInfo Unknown(string text) =>
        new(MinigrafNativeErrorCategory.Unknown, null, text);

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        if (root.TryGetProperty(name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Structural code shape: <c>PREFIX-NNN</c> (2-8 uppercase
    /// letters, dash, three digits). Codes are never derived from text.</summary>
    private static bool IsStructuredCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        int dash = code.IndexOf('-');
        if (dash < 2 || dash > 8 || code.Length != dash + 4)
        {
            return false;
        }

        for (int i = 0; i < dash; i++)
        {
            if (code[i] < 'A' || code[i] > 'Z')
            {
                return false;
            }
        }

        for (int i = dash + 1; i < code.Length; i++)
        {
            if (code[i] < '0' || code[i] > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static MinigrafNativeErrorCategory ParseCategory(string? text) => text switch
    {
        "Parser" => MinigrafNativeErrorCategory.Parser,
        "Query" => MinigrafNativeErrorCategory.Query,
        "Storage" => MinigrafNativeErrorCategory.Storage,
        "Wal" => MinigrafNativeErrorCategory.Wal,
        "Api" => MinigrafNativeErrorCategory.Api,
        "Internal" => MinigrafNativeErrorCategory.Internal,
        "Shim" => MinigrafNativeErrorCategory.Shim,
        _ => MinigrafNativeErrorCategory.Unknown,
    };
}
