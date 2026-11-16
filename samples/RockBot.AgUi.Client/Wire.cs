using System.Text.Json;
using System.Text.Json.Serialization;
using RockBot.UserProxy;

namespace RockBot.AgUi.Client;

// The RockBot payloads inside AG-UI events and REST answers, as docs/agui.md describes them. All
// are camelCase JSON. They are read through a source-generated context, so nothing here needs
// reflection in a trimmed browser app.

/// <summary>An attachment as events, history and uploads list it: the reference plus the URL of its bytes.</summary>
public sealed record WireAttachment
{
    public string Mime { get; init; } = "";
    public string Path { get; init; } = "";
    public string? FileName { get; init; }

    /// <summary>Relative to the proxy's address.</summary>
    public string? Url { get; init; }

    public AgentAttachment ToAttachment() => new() { Mime = Mime, Path = Path, FileName = FileName };
}

internal sealed record AttachmentsValue
{
    public string? MessageId { get; init; }
    public List<WireAttachment>? Attachments { get; init; }
}

internal sealed record OriginValue
{
    public string? MessageId { get; init; }

    /// <summary>The session the reply was sent in, when the proxy says (it tells scheduled-task output apart).</summary>
    public string? SessionId { get; init; }
    public ReplyOrigin? Origin { get; init; }
    public ReplySource? Source { get; init; }
    public string? FollowsQuestionId { get; init; }
}

internal sealed record QuestionValue
{
    public string? SessionId { get; init; }
    public string? AgentName { get; init; }
    public JsonElement? Form { get; init; }
    public string? Text { get; init; }
}

internal sealed record RunValue
{
    public string? RunId { get; init; }
    public string? AgentName { get; init; }
    public string? Status { get; init; }
    public string? Outcome { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
}

internal sealed record ActivityContent
{
    public string? AgentName { get; init; }
    public string? Text { get; init; }
    public bool Done { get; init; }
}

internal sealed record ToolResultMetadata
{
    public ToolResultDetail? Rockbot { get; init; }
}

internal sealed record ToolResultDetail
{
    public bool Failed { get; init; }
    public long? DurationMs { get; init; }
    public string? AgentName { get; init; }
}

internal sealed record ToolArgs
{
    public string? Summary { get; init; }
}

internal sealed record ActiveWorkValue
{
    public List<ActiveWorkItem>? Work { get; init; }
}

internal sealed record NameValue
{
    public string? AgentName { get; init; }
    public string? Message { get; init; }
}

internal sealed record StateDocument
{
    public Dictionary<string, List<TaskListItem>>? TaskLists { get; init; }
}

internal sealed record PatchOperation
{
    public string? Op { get; init; }
    public string? Path { get; init; }
    public JsonElement? Value { get; init; }
}

internal sealed record HistorySnapshot
{
    public List<HistoryMessage>? Messages { get; init; }
}

internal sealed record HistoryMessage
{
    public string? Id { get; init; }
    public string? Role { get; init; }
    public JsonElement? Content { get; init; }
    public string? Name { get; init; }
    public HistoryMetadata? Metadata { get; init; }
}

internal sealed record HistoryMetadata
{
    public HistoryDetail? Rockbot { get; init; }
}

internal sealed record HistoryDetail
{
    public DateTimeOffset? Timestamp { get; init; }
    public string? AgentName { get; init; }
    public List<WireAttachment>? Attachments { get; init; }

    /// <summary>The session the turn is from: the thread, or a broadcast session merged into it (#809).</summary>
    public string? SessionId { get; init; }
    public ReplyOrigin? Origin { get; init; }
    public ReplySource? Source { get; init; }

    /// <summary>The session the original was in, on a copy of another session's reply (#841).</summary>
    public string? ImportedFromSessionId { get; init; }
}

internal sealed record QuestionsResponse
{
    public List<JsonElement>? Questions { get; init; }
}

/// <summary><c>GET /agent</c>.</summary>
public sealed record AgentInfo
{
    public string AgentName { get; init; } = "";
    public string AgentVersion { get; init; } = "";
    public bool IsProcessing { get; init; }
    public List<ActiveWorkItem>? Work { get; init; }
}

/// <summary><c>POST /tickets</c>. In network mode only <see cref="Url"/> is set.</summary>
public sealed record TicketResponse
{
    public string Url { get; init; } = "";
    public string? Ticket { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

internal sealed record TicketRequest
{
    public string Kind { get; init; } = "";
    public string? Path { get; init; }
    public string? ThreadId { get; init; }
}

internal sealed record ProblemBody
{
    public string? Title { get; init; }
    public string? Detail { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireAttachment))]
[JsonSerializable(typeof(AttachmentsValue))]
[JsonSerializable(typeof(OriginValue))]
[JsonSerializable(typeof(QuestionValue))]
[JsonSerializable(typeof(RunValue))]
[JsonSerializable(typeof(ActivityContent))]
[JsonSerializable(typeof(ToolResultMetadata))]
[JsonSerializable(typeof(ToolArgs))]
[JsonSerializable(typeof(ActiveWorkValue))]
[JsonSerializable(typeof(NameValue))]
[JsonSerializable(typeof(StateDocument))]
[JsonSerializable(typeof(List<PatchOperation>))]
[JsonSerializable(typeof(HistorySnapshot))]
[JsonSerializable(typeof(QuestionsResponse))]
[JsonSerializable(typeof(AgentInfo))]
[JsonSerializable(typeof(TicketResponse))]
[JsonSerializable(typeof(TicketRequest))]
[JsonSerializable(typeof(ProblemBody))]
[JsonSerializable(typeof(McpQuestionForm))]
[JsonSerializable(typeof(McpQuestionStatus))]
[JsonSerializable(typeof(SubagentStarted))]
[JsonSerializable(typeof(SubagentFinished))]
[JsonSerializable(typeof(TaskListItem))]
internal sealed partial class WireJson : JsonSerializerContext;
