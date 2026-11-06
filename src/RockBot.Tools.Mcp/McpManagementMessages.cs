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

    /// <summary>
    /// A subagent passes the question up to the user through its primary agent instead of
    /// answering it: the question is re-bound to the primary's session and this request waits for
    /// the resumed call's outcome, as an answer would. Only a subagent's session may escalate.
    /// </summary>
    public bool Escalate { get; init; }

    /// <summary>
    /// With <see cref="Escalate"/>: the consequential-action origin of the escalating run, so the
    /// primary's turns about the question run under the same authorization (#685).
    /// </summary>
    public string? RunOrigin { get; init; }

    /// <summary>With <see cref="Escalate"/>: what the originating user request asked for.</summary>
    public string? UserAskedFor { get; init; }
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
    /// The answer went to the server, but the call doesn't finish in this reply: <see cref="Result"/>
    /// only says so. Either the question came from a background task, whose result arrives later
    /// as an <see cref="McpTaskResult"/>, or a subagent escalated it and still waits, so the
    /// result goes back to that subagent.
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

/// <summary>
/// A subagent stopped waiting for a question it escalated (its wait timed out, or its run ended):
/// the resumed call's outcome goes to the primary agent instead. Only the session that escalated
/// the question may detach it. Fire-and-forget; no reply. Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpDetachEscalationRequest
{
    public required string QuestionId { get; init; }

    /// <summary>The subagent session that escalated the question.</summary>
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

// ── Resource watches ─────────────────────────────────────────────────────────

/// <summary>
/// <c>mcp_watch_resource</c>: have the bridge tell this session, as a new turn, when a server
/// resource changes. Published to <c>mcp.manage</c>.
/// </summary>
public sealed record McpWatchResourceRequest
{
    public required string ServerName { get; init; }

    /// <summary>The resource URI as the server lists it, any template expanded.</summary>
    public required string Uri { get; init; }

    /// <summary>Session of the run asking; updates are delivered to it.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The consequential-action origin of the run asking (#685), so a turn an update starts runs
    /// under that run's authorization.
    /// </summary>
    public string? RunOrigin { get; init; }

    /// <summary>With a user-turn <see cref="RunOrigin"/>: what the originating request asked for.</summary>
    public string? UserAskedFor { get; init; }
}

/// <summary>
/// <c>mcp_unwatch_resource</c>: stop watching a resource this session watches. Published to
/// <c>mcp.manage</c>.
/// </summary>
public sealed record McpUnwatchResourceRequest
{
    public required string ServerName { get; init; }

    public required string Uri { get; init; }

    public string? SessionId { get; init; }
}

/// <summary>The bridge's answer to <see cref="McpWatchResourceRequest"/> and <see cref="McpUnwatchResourceRequest"/>.</summary>
public sealed record McpWatchResourceResponse
{
    public required string ServerName { get; init; }

    public required string Uri { get; init; }

    /// <summary>The watch's id (<c>w_…</c>), when there is one.</summary>
    public string? WatchId { get; init; }

    /// <summary>What happened, for the agent.</summary>
    public string? Message { get; init; }

    /// <summary>The request was refused, and why.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Something a background MCP task has for the session that started it, published by the bridge
/// to <c>mcp.task.result.{agent}</c>: the task's result or how it ended, or a question the server
/// asked mid-task that the session must answer with <c>mcp_answer</c>. The same delivery carries a
/// question a subagent escalated to its primary session, and that call's result when the subagent
/// is no longer there to take it (<see cref="KindEscalatedQuestion"/>, <see cref="KindEscalatedResult"/>).
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

    /// <summary>
    /// A subagent escalated a server's question to the user (<c>mcp_answer</c> with
    /// <c>escalate</c>); <see cref="Content"/> tells the primary agent about it, and
    /// <see cref="TaskId"/> is the subagent's task id. Not a background task's.
    /// </summary>
    public const string KindEscalatedQuestion = "escalated-question";

    /// <summary>
    /// The call behind an escalated question finished after the subagent that escalated it stopped
    /// waiting: <see cref="Content"/> is its result, folded back to the primary agent.
    /// <see cref="TaskId"/> is the subagent's task id.
    /// </summary>
    public const string KindEscalatedResult = "escalated-result";

    /// <summary>
    /// A resource the session watches (<c>mcp_watch_resource</c>) changed: <see cref="Content"/>
    /// is its new content, <see cref="TaskId"/> the watch id (<c>w_…</c>), <see cref="ResourceUri"/>
    /// the resource and <see cref="ContentHash"/> which version it is.
    /// </summary>
    public const string KindResourceUpdated = "resource-updated";

    /// <summary>
    /// A watch ended without the agent asking (the resource is gone, the watch expired, the server
    /// was removed): <see cref="Content"/> says why. <see cref="TaskId"/> is the watch id.
    /// </summary>
    public const string KindWatchEnded = "watch-ended";

    /// <summary>The run namespace that started the task: <c>session/{id}</c> or <c>subagent/{id}</c>.</summary>
    public required string SessionId { get; init; }

    /// <summary>The bridge's id for the task (<c>t_…</c>), as the "started" result gave it.</summary>
    public required string TaskId { get; init; }

    public required string ServerName { get; init; }

    public required string ToolName { get; init; }

    /// <summary>
    /// One of <see cref="KindCompleted"/>, <see cref="KindEnded"/>, <see cref="KindQuestion"/>,
    /// <see cref="KindEscalatedQuestion"/>, <see cref="KindEscalatedResult"/>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>Final task status for <see cref="KindEnded"/> (failed, cancelled, lost, timed-out).</summary>
    public string? Status { get; init; }

    /// <summary>The text for the session: the mapped tool result, the ending, or the question.</summary>
    public string Content { get; init; } = "";

    /// <summary>The result is an error result, or the task ended without one.</summary>
    public bool IsError { get; init; }

    /// <summary>For <see cref="KindQuestion"/> and <see cref="KindEscalatedQuestion"/>: the question's id.</summary>
    public string? QuestionId { get; init; }

    /// <summary>For a question: the user also sees it as a form in their chat.</summary>
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

    /// <summary>For a watch's delivery: the watched resource's URI.</summary>
    public string? ResourceUri { get; init; }

    /// <summary>
    /// For <see cref="KindResourceUpdated"/>: the hash of the content delivered, so each version is
    /// handled once although delivery is at least once.
    /// </summary>
    public string? ContentHash { get; init; }
}
