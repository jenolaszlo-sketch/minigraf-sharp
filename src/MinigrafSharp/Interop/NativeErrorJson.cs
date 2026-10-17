using System.Text.Json;

namespace MinigrafSharp.Interop;

/// <summary>Structured error payload copied from an owned shim JSON string.</summary>
internal readonly record struct NativeErrorInfo(
    MinigrafNativeErrorCategory Category,
    string? Code,
    string Message);

/// <summary>
/// Reads and frees the owned structured-error JSON produced by the shim
/// (<c>mgs_last_error_json</c> / <c>mgs_tx_last_error_json</c>). The payload is
/// <c>{"category","code","message"}</c>; malformed payloads degrade to
/// <see cref="MinigrafNativeErrorCategory.Unknown"/> with the diagnostic text
/// preserved, never to a guessed category.
/// </summary>
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
            string? text = NativeUtf8.ReadCString(ownedJson);
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
                    return new NativeErrorInfo(MinigrafNativeErrorCategory.Unknown, null, text);
                }

                string? categoryText = ReadString(root, "category");
                string? code = ReadString(root, "code");
                string message = ReadString(root, "message") ?? text;
                return new NativeErrorInfo(ParseCategory(categoryText), code, message);
            }
            catch (JsonException)
            {
                return new NativeErrorInfo(MinigrafNativeErrorCategory.Unknown, null, text);
            }
        }
        finally
        {
            ShimNativeMethods.MgsStringFree(ownedJson);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
