using System.Text.Encodings.Web;
using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// AG-UI JSON as RockBot writes it: the SDK's own serializer context, so the shape is the SDK's,
/// with a relaxed encoder that keeps arrows, quotes and markup readable. The output is still
/// valid JSON on one line.
/// </summary>
public static class AgUiJson
{
    /// <summary>The SDK's serializer context with the relaxed encoder.</summary>
    public static AGUIJsonSerializerContext Context { get; } = new(
        new JsonSerializerOptions(AGUIJsonSerializerContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>One event as a single-line JSON object.</summary>
    public static string Serialize(BaseEvent e) => JsonSerializer.Serialize(e, Context.BaseEvent);
}
