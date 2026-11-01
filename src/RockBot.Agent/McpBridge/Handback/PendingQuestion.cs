using ModelContextProtocol.Protocol;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge.Handback;

/// <summary>
/// Where a call's outcome goes. The original tool call's reply topic for its first stretch, then
/// the reply topic of each <c>mcp_answer</c> that resumed it.
/// </summary>
/// <param name="ReplyTo">Topic to publish to.</param>
/// <param name="CorrelationId">Correlation id of the request being answered.</param>
/// <param name="ToolCallId">Tool call id the response carries.</param>
/// <param name="ToolName">Tool name the response carries.</param>
/// <param name="AnsweringQuestionId">
/// Set when the request was an <c>mcp_answer</c> for this question: the outcome is wrapped in an
/// <see cref="RockBot.Tools.Mcp.McpAnswerQuestionResponse"/>. Null for the original tool call.
/// </param>
/// <param name="CallServer">Server of the call being resumed, reported with an answer's outcome.</param>
/// <param name="CallTool">Tool of the call being resumed, reported with an answer's outcome.</param>
/// <param name="Escalation">
/// Set when the target is a subagent waiting on a question it escalated: the outcome goes to it
/// only while it still waits, and is folded back to its primary session otherwise.
/// </param>
internal sealed record HandbackReplyTarget(
    string ReplyTo,
    string? CorrelationId,
    string ToolCallId,
    string ToolName,
    string? AnsweringQuestionId = null,
    string? CallServer = null,
    string? CallTool = null,
    QuestionEscalation? Escalation = null);

/// <summary>
/// A question a subagent passed up to the user through its primary agent
/// (<c>mcp_answer</c> with <c>escalate</c>). The subagent waits on <see cref="WaitingTarget"/> for
/// the resumed call's outcome until it <see cref="Detach">detaches</see> — its wait timed out or its
/// run ended — after which the outcome goes to the primary session.
/// </summary>
internal sealed class QuestionEscalation
{
    private int _detached;

    /// <param name="waitingTarget">The subagent's escalating <c>mcp_answer</c>; its escalation is set to this one.</param>
    public QuestionEscalation(
        string subagentSessionId, string primarySessionId, HandbackReplyTarget waitingTarget,
        string? runOrigin, string? userAskedFor)
    {
        SubagentSessionId = subagentSessionId;
        PrimarySessionId = primarySessionId;
        WaitingTarget = waitingTarget with { Escalation = this };
        RunOrigin = runOrigin;
        UserAskedFor = userAskedFor;
    }

    /// <summary>The subagent's run namespace (<c>subagent/{taskId}</c>) that escalated the question.</summary>
    public string SubagentSessionId { get; }

    /// <summary>The primary user session (<c>session/{id}</c>) the question was re-bound to.</summary>
    public string PrimarySessionId { get; }

    /// <summary>The subagent's waiting <c>mcp_answer</c>, with <see cref="HandbackReplyTarget.Escalation"/> set to this.</summary>
    public HandbackReplyTarget WaitingTarget { get; }

    /// <summary>The escalating run's consequential-action origin, for the primary's turns (#685).</summary>
    public string? RunOrigin { get; }

    public string? UserAskedFor { get; }

    /// <summary>The subagent's task id: its run namespace without the <c>subagent/</c> prefix.</summary>
    public string SubagentTaskId => HandbackSessions.SubagentTaskId(SubagentSessionId);

    /// <summary>The subagent stopped waiting: the outcome goes to the primary session.</summary>
    public bool IsDetached => Volatile.Read(ref _detached) == 1;

    /// <summary>Marks the subagent as no longer waiting. False when it already was.</summary>
    public bool Detach() => Interlocked.Exchange(ref _detached, 1) == 0;
}

/// <summary>
/// What settled a pending question: the agent's answer and where the resumed call reports, or —
/// with no <see cref="Target"/> — the question was abandoned (expired, its server removed, the
/// bridge stopping) and nobody is waiting for the call. <see cref="FromUser"/> marks an answer the
/// user gave in the chat's form rather than the agent through <c>mcp_answer</c>; it labels the
/// outcome and changes no check.
/// </summary>
internal sealed record McpAnswerSubmission(McpHandbackOutcome Outcome, HandbackReplyTarget? Target, bool FromUser = false);

/// <summary>A question handed back to the agent and not yet settled.</summary>
internal sealed class PendingQuestion
{
    private readonly TaskCompletionSource<McpAnswerSubmission> _submission =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Random, unguessable id the agent answers with. The server never sees it.</summary>
    public required string QuestionId { get; init; }

    /// <summary>
    /// Session the question is bound to; only it may answer. The session that made the call, or —
    /// once a subagent escalates the question — its primary session.
    /// </summary>
    public required string? SessionId { get; set; }

    /// <summary>
    /// When the question was put to its current session: <see cref="CreatedAt"/>, or the moment it
    /// was escalated. A decision needs a person turn after this.
    /// </summary>
    public DateTimeOffset AskedAt { get; set; }

    /// <summary>Set once a subagent escalates the question to its primary session.</summary>
    public QuestionEscalation? Escalation { get; set; }

    public required string ServerName { get; init; }

    public required string ToolName { get; init; }

    /// <summary>The server's question and form, as it sent them.</summary>
    public required ElicitRequestParams Request { get; init; }

    public required int Round { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The parked call, or background task, this question belongs to.</summary>
    public required IHandbackOwner Call { get; init; }

    /// <summary>Completes once the question is answered or abandoned.</summary>
    public Task<McpAnswerSubmission> Submission => _submission.Task;

    /// <summary>Settles the question. False when it already was: a question is answered once.</summary>
    public bool TrySubmit(McpAnswerSubmission submission) => _submission.TrySetResult(submission);
}
