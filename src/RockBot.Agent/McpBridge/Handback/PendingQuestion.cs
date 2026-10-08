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
internal sealed record HandbackReplyTarget(
    string ReplyTo,
    string? CorrelationId,
    string ToolCallId,
    string ToolName,
    string? AnsweringQuestionId = null,
    string? CallServer = null,
    string? CallTool = null);

/// <summary>
/// What settled a pending question: the agent's answer and where the resumed call reports, or —
/// with no <see cref="Target"/> — the question was abandoned (expired, its server removed, the
/// bridge stopping) and nobody is waiting for the call.
/// </summary>
internal sealed record McpAnswerSubmission(McpHandbackOutcome Outcome, HandbackReplyTarget? Target);

/// <summary>A question handed back to the agent and not yet settled.</summary>
internal sealed class PendingQuestion
{
    private readonly TaskCompletionSource<McpAnswerSubmission> _submission =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Random, unguessable id the agent answers with. The server never sees it.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Session that made the call; only it may answer.</summary>
    public required string? SessionId { get; init; }

    public required string ServerName { get; init; }

    public required string ToolName { get; init; }

    /// <summary>The server's question and form, as it sent them.</summary>
    public required ElicitRequestParams Request { get; init; }

    public required int Round { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The parked call this question belongs to.</summary>
    public required HandbackCall Call { get; init; }

    /// <summary>Completes once the question is answered or abandoned.</summary>
    public Task<McpAnswerSubmission> Submission => _submission.Task;

    /// <summary>Settles the question. False when it already was: a question is answered once.</summary>
    public bool TrySubmit(McpAnswerSubmission submission) => _submission.TrySetResult(submission);
}
