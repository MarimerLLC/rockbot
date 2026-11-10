using System.Text.Json;

namespace RockBot.Tools;

/// <summary>
/// Helpers for reading tool-call arguments that a model may have sent in the wrong JSON shape.
/// </summary>
public static class ToolArgumentJson
{
    /// <summary>
    /// How many layers of string quoting <see cref="UnwrapStringified"/> reads through. One is
    /// the usual mistake; a model has been seen live to encode twice.
    /// </summary>
    private const int MaxDepth = 3;

    /// <summary>
    /// Undoes a model's habit of sending an array or object parameter as a JSON <em>string</em>
    /// holding that array or object — common on the first call to a deferred tool, before the
    /// model has seen the schema (#782). If <paramref name="element"/> is a string whose trimmed
    /// text starts with <c>[</c> or <c>{</c> and parses as an array or object, the parsed element
    /// is returned. A string encoded more than once (a JSON string holding a JSON string holding
    /// the array) is read through as well, up to a few layers. Anything else comes back unchanged,
    /// so the caller's own validation reports it. Never throws.
    /// </summary>
    public static JsonElement UnwrapStringified(JsonElement element)
    {
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        for (var depth = 0; depth < MaxDepth && !string.IsNullOrEmpty(text); depth++)
        {
            var trimmed = text.AsSpan().TrimStart();
            if (trimmed.IsEmpty || trimmed[0] is not ('[' or '{' or '"'))
                return element;

            try
            {
                using var doc = JsonDocument.Parse(text);
                switch (doc.RootElement.ValueKind)
                {
                    case JsonValueKind.Array or JsonValueKind.Object:
                        return doc.RootElement.Clone();
                    case JsonValueKind.String:
                        text = doc.RootElement.GetString();
                        continue;
                    default:
                        return element;
                }
            }
            catch (JsonException)
            {
                return element;
            }
        }

        return element;
    }
}
