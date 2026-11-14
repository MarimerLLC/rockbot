using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// What <c>POST /agui</c> does with a <see cref="RunAgentInput"/>: either the
/// <see cref="UserMessage"/> to send, or the HTTP error to answer with before any event is streamed.
/// </summary>
internal sealed record AgUiRunRequest
{
    public UserMessage? Message { get; init; }

    /// <summary>The HTTP status to refuse the request with; 0 when <see cref="Message"/> is set.</summary>
    public int ErrorStatus { get; init; }

    public string? Error { get; init; }

    public static AgUiRunRequest Refuse(int status, string error) => new() { ErrorStatus = status, Error = error };
}

/// <summary>Maps an AG-UI run request onto RockBot's <see cref="UserMessage"/>. Pure, so it is tested directly.</summary>
internal static class AgUiRequestMapper
{
    /// <summary>The <c>forwardedProps</c> object RockBot reads.</summary>
    public const string ForwardedPropsKey = "rockbot";

    /// <summary>The property of <c>forwardedProps.rockbot</c> listing <see cref="ClientCapabilities"/> names.</summary>
    public const string CapabilitiesKey = "capabilities";

    public static AgUiRunRequest Map(RunAgentInput input, AgUiOptions options, string channelName)
    {
        if (string.IsNullOrWhiteSpace(input.ThreadId))
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, "threadId is required.");
        if (string.IsNullOrWhiteSpace(input.RunId))
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, "runId is required.");

        if (!options.IsThreadAllowed(input.ThreadId))
            return AgUiRunRequest.Refuse(StatusCodes.Status403Forbidden, $"Thread '{input.ThreadId}' is not allowed.");

        if (input.Resume is { Count: > 0 })
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, "Resuming an interrupted run is not supported yet.");

        // The agent owns the conversation history, so only the newest user message is sent;
        // whatever else the client replays is ignored.
        var text = LastUserText(input.Messages);
        if (string.IsNullOrWhiteSpace(text))
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, "messages must contain a user message with text.");

        return new AgUiRunRequest
        {
            Message = new UserMessage
            {
                Content = text,
                SessionId = input.ThreadId,
                UserId = options.DefaultUserId,
                ChannelName = channelName,
                ClientCapabilities = Capabilities(input.ForwardedProperties),
            },
        };
    }

    /// <summary>
    /// The text of the last user message, or null without one. Image and document parts are
    /// dropped: attachments are not part of this endpoint yet.
    /// </summary>
    internal static string? LastUserText(IList<AGUIMessage>? messages)
    {
        var last = messages?.OfType<AGUIUserMessage>().LastOrDefault();
        return last is null || last.Content.Value is null ? null : last.Content.ToString();
    }

    /// <summary>
    /// The capabilities a client declared in <c>forwardedProps.rockbot.capabilities</c>, an array
    /// of <see cref="ClientCapabilities"/> names (case-insensitive; names this proxy doesn't know
    /// are ignored). Without that array, <see cref="ClientCapabilityPresets.AgUi"/>.
    /// </summary>
    internal static ClientCapabilities Capabilities(JsonElement? forwardedProps)
    {
        if (forwardedProps is not { ValueKind: JsonValueKind.Object } props
            || !props.TryGetProperty(ForwardedPropsKey, out var rockbot)
            || rockbot.ValueKind != JsonValueKind.Object
            || !rockbot.TryGetProperty(CapabilitiesKey, out var declared)
            || declared.ValueKind != JsonValueKind.Array)
        {
            return ClientCapabilityPresets.AgUi;
        }

        // Every client can show text, whatever it listed.
        var capabilities = ClientCapabilities.Text;
        foreach (var item in declared.EnumerateArray())
        {
            // Names only: Enum.TryParse would also take a number, and with it any bit at all.
            if (item.ValueKind == JsonValueKind.String
                && item.GetString() is { Length: > 0 } name
                && !char.IsAsciiDigit(name[0]) && name[0] != '-' && name[0] != '+'
                && !name.Contains(',')
                && Enum.TryParse<ClientCapabilities>(name, ignoreCase: true, out var capability))
            {
                capabilities |= capability;
            }
        }
        return capabilities;
    }
}
