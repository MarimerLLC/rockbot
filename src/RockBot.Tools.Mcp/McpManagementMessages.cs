namespace RockBot.Tools.Mcp;

// ── GetServiceDetails ────────────────────────────────────────────────────────

/// <summary>
/// Requests the full tool schema list for one MCP server from the bridge.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpGetServiceDetailsRequest
{
    public required string ServerName { get; init; }
}

/// <summary>
/// Bridge response carrying all tool and prompt definitions for the requested server,
/// plus the server's self-reported identity from the MCP <c>initialize</c> handshake.
/// </summary>
public sealed record McpGetServiceDetailsResponse
{
    public required string ServerName { get; init; }

    /// <summary>Stable id of the server entry (see <see cref="McpServerSummary.ServerId"/>).</summary>
    public string? ServerId { get; init; }

    /// <summary>Surface fingerprint (see <see cref="McpServerSummary.Fingerprint"/>).</summary>
    public string? Fingerprint { get; init; }

    /// <summary>Server's self-reported implementation name (from <c>initialize.result.serverInfo.name</c>).</summary>
    public string? ImplementationName { get; init; }

    /// <summary>Server's self-reported display title.</summary>
    public string? Title { get; init; }

    /// <summary>Server's self-reported version string.</summary>
    public string? Version { get; init; }

    /// <summary>Server's self-reported implementation description.</summary>
    public string? Description { get; init; }

    /// <summary>Free-text usage instructions supplied by the server during initialize.</summary>
    public string? Instructions { get; init; }

    public List<McpToolDefinition> Tools { get; init; } = [];
    public List<McpPromptDefinition> Prompts { get; init; } = [];

    /// <summary>Resources plus resource templates the server lists; read them with <c>mcp_list_resources</c>.</summary>
    public int ResourceCount { get; init; }

    public string? Error { get; init; }
}

// ── RegisterServer ───────────────────────────────────────────────────────────

/// <summary>
/// Requests the bridge to connect a new MCP server at runtime.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpRegisterServerRequest
{
    public required string ServerName { get; init; }
    public required string Type { get; init; }
    public string? Url { get; init; }
    public string? Command { get; init; }
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
}

/// <summary>Bridge response confirming or reporting failure for a server registration.</summary>
public sealed record McpRegisterServerResponse
{
    public required string ServerName { get; init; }
    public bool Success { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
}

// ── UnregisterServer ─────────────────────────────────────────────────────────

/// <summary>
/// Requests the bridge to disconnect and remove an MCP server at runtime.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpUnregisterServerRequest
{
    public required string ServerName { get; init; }
}

/// <summary>Bridge response confirming or reporting failure for a server removal.</summary>
public sealed record McpUnregisterServerResponse
{
    public required string ServerName { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

// ── GetPrompt ────────────────────────────────────────────────────────────────

/// <summary>
/// Requests a filled-in prompt template from an MCP server.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpGetPromptRequest
{
    public required string ServerName { get; init; }
    public required string PromptName { get; init; }
    public Dictionary<string, string> Arguments { get; init; } = [];
}

/// <summary>Bridge response carrying the filled-in prompt messages.</summary>
public sealed record McpGetPromptResponse
{
    public required string ServerName { get; init; }
    public required string PromptName { get; init; }
    public string? Description { get; init; }
    public List<McpPromptMessage> Messages { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>A single message from a filled-in MCP prompt template.</summary>
public sealed record McpPromptMessage
{
    public required string Role { get; init; }       // "user" or "assistant"
    public required string Content { get; init; }    // text content (most common case)
    public string ContentType { get; init; } = "text";
}

// ── ListResources ────────────────────────────────────────────────────────────

/// <summary>
/// Requests the resources and resource templates an MCP server lists (#617).
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpListResourcesRequest
{
    public required string ServerName { get; init; }
}

/// <summary>Bridge response carrying a server's resources and resource templates.</summary>
public sealed record McpListResourcesResponse
{
    public required string ServerName { get; init; }
    public List<McpResourceDefinition> Resources { get; init; } = [];
    public List<McpResourceDefinition> Templates { get; init; } = [];
    public string? Error { get; init; }
}

// ── ReadResource ─────────────────────────────────────────────────────────────

/// <summary>
/// Requests one resource from an MCP server by the server's own URI, any template expanded
/// (#617). Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpReadResourceRequest
{
    public required string ServerName { get; init; }
    public required string Uri { get; init; }
}

/// <summary>Bridge response carrying a read resource's contents.</summary>
public sealed record McpReadResourceResponse
{
    public required string ServerName { get; init; }
    public required string Uri { get; init; }
    public List<McpResourceContentView> Contents { get; init; } = [];
    public string? Error { get; init; }
}

// ── AnswerQuestion ───────────────────────────────────────────────────────────

/// <summary>
/// <c>mcp_answer</c>: the agent's answer to a question an MCP server asked mid-call and the bridge
/// handed back (see <c>design/mcp-elicitation-handback.md</c>). Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpAnswerQuestionRequest
{
    /// <summary>The <c>question_id</c> from the hand-back.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Session of the run answering; it must be the session that made the original call.</summary>
    public string? SessionId { get; init; }

    /// <summary>Tool call id of the <c>mcp_answer</c> call, for the result it gets back.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>The answer's field values as a JSON object, keyed by the server's field names.</summary>
    public string? Answers { get; init; }

    /// <summary>Refuse the question: the server continues without the value.</summary>
    public bool Decline { get; init; }

    /// <summary>
    /// The user gave this answer in the chat's form, not the agent through <c>mcp_answer</c>. It
    /// labels the outcome only; every check runs as for the agent's answer. The model has no way
    /// to set it.
    /// </summary>
    public bool FromUser { get; init; }
}

/// <summary>
/// What <c>mcp_answer</c> led to. <see cref="Error"/> means the answer was refused (the question
/// is unknown, expired, answered, from another session, or the answer doesn't fit) and nothing
/// was sent to the server. Otherwise <see cref="Result"/> is the resumed call's outcome: its
/// result, or the server's next question, handed back the same way.
/// </summary>
public sealed record McpAnswerQuestionResponse
{
    public required string QuestionId { get; init; }

    /// <summary>Server of the resumed call, for metrics and typed-tool pinning.</summary>
    public string? ServerName { get; init; }

    /// <summary>Tool of the resumed call.</summary>
    public string? ToolName { get; init; }

    public ToolInvokeResponse? Result { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// With <see cref="Error"/>: the question can't be answered any more (unknown, expired,
    /// answered, interrupted), as opposed to an answer that didn't fit and may be corrected.
    /// </summary>
    public bool QuestionClosed { get; init; }

    /// <summary>
    /// The question came from a background task. The answer went to the server, but the task
    /// doesn't finish in this reply: <see cref="Result"/> only says so, and the task's result
    /// arrives later as an <see cref="McpTaskResult"/>.
    /// </summary>
    public bool TaskContinues { get; init; }
}

/// <summary>
/// A run's session has ended for good (a subagent finished): the bridge releases any question it
/// handed back to that session and cancels the parked call. Fire-and-forget; no reply.
/// Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpReleaseSessionQuestionsRequest
{
    public required string SessionId { get; init; }
}

// ── Background tasks ─────────────────────────────────────────────────────────

/// <summary>
/// <c>mcp_cancel_task</c>: stop a background task this session started. Published to
/// <c>mcp.manage</c>.
/// </summary>
public sealed record McpCancelTaskRequest
{
    /// <summary>The task id from the "started" result (<c>t_…</c>).</summary>
    public required string TaskId { get; init; }

    /// <summary>Session of the run cancelling; only the session that started the task may.</summary>
    public string? SessionId { get; init; }
}

public sealed record McpCancelTaskResponse
{
    public required string TaskId { get; init; }

    /// <summary>What happened, for the agent.</summary>
    public string? Message { get; init; }

    /// <summary>The task couldn't be cancelled (unknown, another session's, or already finished).</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Something a background MCP task has for the session that started it, published by the bridge
/// to <c>mcp.task.result.{agent}</c>: the task's result or how it ended, or a question the server
/// asked mid-task that the session must answer with <c>mcp_answer</c>.
/// </summary>
/// <remarks>
/// Delivery is at least once: a restart between publishing and recording the delivery repeats it,
/// and <see cref="TaskId"/> (plus <see cref="QuestionId"/>) lets a repeat be recognized.
/// </remarks>
public sealed record McpTaskResult
{
    /// <summary>Topic prefix; the agent's name follows.</summary>
    public const string TopicPrefix = "mcp.task.result";

    /// <summary>The task's result is in <see cref="Content"/>.</summary>
    public const string KindCompleted = "completed";

    /// <summary>The task ended without a result (failed, cancelled, lost, timed out): <see cref="Content"/> says how.</summary>
    public const string KindEnded = "ended";

    /// <summary>The server asked a question mid-task; <see cref="Content"/> is the hand-back text.</summary>
    public const string KindQuestion = "question";

    /// <summary>The run namespace that started the task: <c>session/{id}</c> or <c>subagent/{id}</c>.</summary>
    public required string SessionId { get; init; }

    /// <summary>The bridge's id for the task (<c>t_…</c>), as the "started" result gave it.</summary>
    public required string TaskId { get; init; }

    public required string ServerName { get; init; }

    public required string ToolName { get; init; }

    /// <summary>One of <see cref="KindCompleted"/>, <see cref="KindEnded"/>, <see cref="KindQuestion"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Final task status for <see cref="KindEnded"/> (failed, cancelled, lost, timed-out).</summary>
    public string? Status { get; init; }

    /// <summary>The text for the session: the mapped tool result, the ending, or the question.</summary>
    public string Content { get; init; } = "";

    /// <summary>The result is an error result, or the task ended without one.</summary>
    public bool IsError { get; init; }

    /// <summary>For <see cref="KindQuestion"/>: the question's id.</summary>
    public string? QuestionId { get; init; }

    /// <summary>For <see cref="KindQuestion"/>: the user also sees the question as a form in their chat.</summary>
    public bool FormShown { get; init; }

    /// <summary>
    /// Where the bridge already stored <see cref="Content"/> in the session's working memory, or
    /// null when it couldn't (the handler stores it then). Stored before publishing, so a
    /// subagent that waits for its tasks finds the result among its outputs.
    /// </summary>
    public string? WorkingMemoryKey { get; init; }

    /// <summary>
    /// The consequential-action origin of the run that started the task (<c>user-turn</c>,
    /// <c>subagent-of-user-turn</c>, <c>scheduled</c>, …), so the turn that answers its result
    /// runs under the same authorization (#685). Null when the run didn't state one.
    /// </summary>
    public string? RunOrigin { get; init; }

    /// <summary>
    /// With a user-turn <see cref="RunOrigin"/>: whether the originating user request asked for
    /// anything (<c>instruction</c> or <c>information-only</c>).
    /// </summary>
    public string? UserAskedFor { get; init; }

    /// <summary>
    /// Whether the session should get a reply. False for an ending the agent asked for itself
    /// (<c>mcp_cancel_task</c>): it's recorded in the conversation, but there's nothing to say.
    /// </summary>
    public bool NeedsReply { get; init; } = true;
}
