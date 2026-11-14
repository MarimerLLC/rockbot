using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>One entry of a run's <c>resume</c>: the answer to one interrupt, which is one MCP question.</summary>
/// <param name="QuestionId">The interrupt's id.</param>
/// <param name="Decline">True for <c>cancelled</c>: the user refused the question.</param>
/// <param name="Answers">For <c>resolved</c>: the payload, a JSON object of field values.</param>
internal sealed record AgUiResumeEntry(string QuestionId, bool Decline, string? Answers);

/// <summary>
/// What <c>POST /agui</c> does with a <see cref="RunAgentInput"/>: send a <see cref="UserMessage"/>,
/// or answer the interrupts in <see cref="Resume"/>, or refuse with an HTTP error before any event
/// is streamed.
/// </summary>
internal sealed record AgUiRunRequest
{
    /// <summary>The message to send. Null for a refusal and for a run that resumes.</summary>
    public UserMessage? Message { get; init; }

    /// <summary>The interrupts this run answers. Null unless the request has <c>resume</c>.</summary>
    public IReadOnlyList<AgUiResumeEntry>? Resume { get; init; }

    /// <summary>How the run takes part in MCP questions.</summary>
    public QuestionMode QuestionMode { get; init; }

    /// <summary>The HTTP status to refuse the request with; 0 when it isn't refused.</summary>
    public int ErrorStatus { get; init; }

    public string? Error { get; init; }

    public bool IsRefused => ErrorStatus != 0;

    public static AgUiRunRequest Refuse(int status, string error) => new() { ErrorStatus = status, Error = error };
}

/// <summary>Maps an AG-UI run request onto RockBot's <see cref="UserMessage"/>. Pure, so it is tested directly.</summary>
internal static class AgUiRequestMapper
{
    /// <summary>The <c>forwardedProps</c> object RockBot reads.</summary>
    public const string ForwardedPropsKey = "rockbot";

    /// <summary>The property of <c>forwardedProps.rockbot</c> listing <see cref="ClientCapabilities"/> names.</summary>
    public const string CapabilitiesKey = "capabilities";

    /// <summary>The property of <c>forwardedProps.rockbot</c> naming the <see cref="QuestionMode"/>: <c>interrupt</c> or <c>custom</c>.</summary>
    public const string QuestionModeKey = "questionMode";

    /// <summary>The property of <c>forwardedProps.rockbot</c> listing the message's attachments, as <c>POST /attachments</c> returned them.</summary>
    public const string AttachmentsKey = "attachments";

    /// <summary>The most files one message may carry.</summary>
    public const int MaxAttachments = 10;

    /// <summary>The longest display name an attachment reference may carry.</summary>
    public const int MaxFileNameLength = 255;

    public static AgUiRunRequest Map(RunAgentInput input, AgUiOptions options, string channelName, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(input.ThreadId))
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, "threadId is required.");
        if (string.IsNullOrWhiteSpace(input.RunId))
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, "runId is required.");

        if (!options.IsThreadAllowed(input.ThreadId))
            return AgUiRunRequest.Refuse(StatusCodes.Status403Forbidden, $"Thread '{input.ThreadId}' is not allowed.");

        if (!TryReadQuestionMode(input.ForwardedProperties, out var declaredMode))
        {
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest,
                $"forwardedProps.{ForwardedPropsKey}.{QuestionModeKey} must be \"interrupt\" or \"custom\".");
        }

        // A run that resumes answers interrupts and sends nothing else. Its messages are ignored:
        // a client replays the conversation with every request, so they say nothing about intent.
        if (input.Resume is { Count: > 0 } resume)
        {
            return MapResume(resume, out var error) is { } entries
                ? new AgUiRunRequest { Resume = entries, QuestionMode = QuestionMode.Interrupt }
                : AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, error);
        }

        // The agent owns the conversation history, so only the newest user message is sent;
        // whatever else the client replays is ignored.
        if (!TryReadAttachments(input.ForwardedProperties, out var attachments, out var attachmentError))
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, attachmentError);

        // A message may be attachments alone ("here, look at this"), as in the Blazor UI.
        var text = LastUserText(input.Messages);
        if (string.IsNullOrWhiteSpace(text) && attachments is null)
            return AgUiRunRequest.Refuse(StatusCodes.Status400BadRequest, "messages must contain a user message with text.");

        // Naming a question mode says the client handles question forms, whatever else it listed.
        var capabilities = Capabilities(input.ForwardedProperties);
        if (declaredMode is not null)
            capabilities |= ClientCapabilities.InteractiveForms;

        return new AgUiRunRequest
        {
            Message = new UserMessage
            {
                Content = text ?? "",
                Attachments = attachments,
                SessionId = input.ThreadId,
                UserId = userId ?? options.DefaultUserId,
                ChannelName = channelName,
                ClientCapabilities = capabilities,
            },
            QuestionMode = ResolveQuestionMode(declaredMode, capabilities),
        };
    }

    /// <summary>
    /// The mode a run with new input works in. Without forms there are no questions to deliver,
    /// so there is no mode. With them it is the one the request named, or <c>interrupt</c>, the
    /// AG-UI way, when it named none.
    /// </summary>
    internal static QuestionMode ResolveQuestionMode(QuestionMode? declared, ClientCapabilities capabilities) =>
        !capabilities.HasFlag(ClientCapabilities.InteractiveForms) ? QuestionMode.None
        : declared ?? QuestionMode.Interrupt;

    /// <summary>
    /// Reads <c>forwardedProps.rockbot.questionMode</c>. False when it is there and isn't one of
    /// the two modes; <paramref name="mode"/> is null when the request doesn't name one.
    /// </summary>
    internal static bool TryReadQuestionMode(JsonElement? forwardedProps, out QuestionMode? mode)
    {
        mode = null;
        if (forwardedProps is not { ValueKind: JsonValueKind.Object } props
            || !props.TryGetProperty(ForwardedPropsKey, out var rockbot)
            || rockbot.ValueKind != JsonValueKind.Object
            || !rockbot.TryGetProperty(QuestionModeKey, out var declared)
            || declared.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        var name = declared.ValueKind == JsonValueKind.String ? declared.GetString() : null;
        if (string.Equals(name, "interrupt", StringComparison.OrdinalIgnoreCase))
            mode = QuestionMode.Interrupt;
        else if (string.Equals(name, "custom", StringComparison.OrdinalIgnoreCase))
            mode = QuestionMode.Custom;
        return mode is not null;
    }

    /// <summary>
    /// The <c>resume</c> entries as question answers, or null with <paramref name="error"/> when
    /// one is malformed: no interrupt id, a status that isn't <c>resolved</c> or <c>cancelled</c>,
    /// a <c>resolved</c> entry whose payload isn't a JSON object, or two entries for one interrupt.
    /// Whether the interrupts are open is not checked here; that takes the proxy's state.
    /// </summary>
    internal static IReadOnlyList<AgUiResumeEntry>? MapResume(IList<AGUIResume> resume, out string error)
    {
        error = "";
        var entries = new List<AgUiResumeEntry>(resume.Count);
        foreach (var item in resume)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.InterruptId))
            {
                error = "Every resume entry needs an interruptId.";
                return null;
            }

            if (entries.Any(e => string.Equals(e.QuestionId, item.InterruptId, StringComparison.Ordinal)))
            {
                error = $"resume answers interrupt '{item.InterruptId}' more than once.";
                return null;
            }

            switch (item.Status)
            {
                case ResumeStatus.Cancelled:
                    entries.Add(new AgUiResumeEntry(item.InterruptId, Decline: true, Answers: null));
                    break;

                case ResumeStatus.Resolved when item.Payload is { ValueKind: JsonValueKind.Object } payload:
                    entries.Add(new AgUiResumeEntry(item.InterruptId, Decline: false, payload.GetRawText()));
                    break;

                case ResumeStatus.Resolved:
                    error = $"The payload of resume entry '{item.InterruptId}' must be a JSON object of field values, as the interrupt's responseSchema describes.";
                    return null;

                default:
                    error = $"The status of resume entry '{item.InterruptId}' must be \"{ResumeStatus.Resolved}\" or \"{ResumeStatus.Cancelled}\".";
                    return null;
            }
        }
        return entries;
    }

    /// <summary>
    /// Reads <c>forwardedProps.rockbot.attachments</c>: the files that go with the message, each a
    /// reference <c>POST /attachments</c> returned (<c>path</c>, <c>mime</c>, optionally
    /// <c>fileName</c>; anything else, such as the <c>url</c> the upload also returned, is
    /// ignored). False with <paramref name="error"/> when the property is there and isn't that.
    /// </summary>
    /// <remarks>
    /// The reference is the client's word for a file on the agent's volume, so it is held to the
    /// form an upload produces. The path must be a plain relative one
    /// (<see cref="AttachmentPathResolver.IsSafeRelativePath"/>): the agent resolves it under the
    /// attachments directory with its own containment check, and this keeps a path that could
    /// mean something else from reaching it at all. The type must be the one the file's extension
    /// has (<see cref="AttachmentMimeTypes"/>), so a client can't have a file read as another
    /// type than its name says. Whether the file exists is the endpoint's check, where the volume is.
    /// </remarks>
    internal static bool TryReadAttachments(
        JsonElement? forwardedProps, out IReadOnlyList<AgentAttachment>? attachments, out string error)
    {
        attachments = null;
        error = "";
        if (forwardedProps is not { ValueKind: JsonValueKind.Object } props
            || !props.TryGetProperty(ForwardedPropsKey, out var rockbot)
            || rockbot.ValueKind != JsonValueKind.Object
            || !rockbot.TryGetProperty(AttachmentsKey, out var declared)
            || declared.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        var where = $"forwardedProps.{ForwardedPropsKey}.{AttachmentsKey}";
        if (declared.ValueKind != JsonValueKind.Array)
        {
            error = $"{where} must be an array of attachment references.";
            return false;
        }

        if (declared.GetArrayLength() > MaxAttachments)
        {
            error = $"{where} has more than {MaxAttachments} entries.";
            return false;
        }

        var list = new List<AgentAttachment>();
        foreach (var item in declared.EnumerateArray())
        {
            var path = item.ValueKind == JsonValueKind.Object ? StringProperty(item, "path") : null;
            if (path is null)
            {
                error = $"Every entry of {where} needs a path, as POST /attachments returned it.";
                return false;
            }

            if (!AttachmentPathResolver.IsSafeRelativePath(path))
            {
                error = $"'{path}' in {where} is not an attachment path. Use the path POST /attachments returned.";
                return false;
            }

            var expected = AttachmentMimeTypes.FromFileName(path);
            var mime = StringProperty(item, "mime");
            if (expected == AttachmentMimeTypes.Unknown || !string.Equals(mime, expected, StringComparison.OrdinalIgnoreCase))
            {
                error = $"The mime of '{path}' in {where} must be the type of its extension"
                    + (expected == AttachmentMimeTypes.Unknown ? ", which is not one that can be attached." : $" ({expected}).");
                return false;
            }

            // The display name is shown to the user and the model, never used as a path.
            var fileName = StringProperty(item, "fileName");
            if (fileName is { Length: > MaxFileNameLength } || fileName?.Any(char.IsControl) == true)
            {
                error = $"The fileName of '{path}' in {where} is not a file name.";
                return false;
            }

            list.Add(new AgentAttachment { Mime = expected, Path = path, FileName = fileName });
        }

        attachments = list.Count > 0 ? list : null;
        return true;
    }

    private static string? StringProperty(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>
    /// The text of the last user message, or null without one. Image and document parts of the
    /// message itself are dropped: files go with a message as references to uploads, in
    /// <c>forwardedProps.rockbot.attachments</c>.
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
