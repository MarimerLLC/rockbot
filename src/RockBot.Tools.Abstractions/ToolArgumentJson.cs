using System.Text.Json;

namespace RockBot.Tools;

/// <summary>
/// Helpers for reading tool-call arguments that a model may have sent in the wrong JSON shape.
/// </summary>
public static class ToolArgumentJson
{
    /// <summary>
    /// Undoes a model's habit of sending an array or object parameter as a JSON <em>string</em>
    /// holding that array or object — common on the first call to a deferred tool, before the
    /// model has seen the schema (#782). If <paramref name="element"/> is a string whose trimmed
    /// text starts with <c>[</c> or <c>{</c> and parses as an array or object, the parsed element
    /// is returned; anything else comes back unchanged, so the caller's own validation reports it.
    /// Never throws.
    /// </summary>
    public static JsonElement UnwrapStringified(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return element;

        var text = element.GetString();
        if (string.IsNullOrEmpty(text))
            return element;

        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '[' && trimmed[0] != '{'))
            return element;

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                ? doc.RootElement.Clone()
                : element;
        }
        catch (JsonException)
        {
            return element;
        }
    }
}
