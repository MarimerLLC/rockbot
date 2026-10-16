using RockBot.Agent.McpBridge.Handback;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge.Tasks;

/// <summary>
/// The hand-back channel of a background task. A task's question doesn't come back through a
/// tool call (that call returned "started" long ago), so the question is delivered to the session
/// as a new turn, like the task's result will be. <c>mcp_answer</c> settles it exactly as it
/// settles a parked call's question, and the bridge sends the answer to the task with
/// <c>tasks/update</c>.
/// </summary>
/// <remarks>
/// Abandoning the question (it expired, its server was removed) only settles the question: the
/// coordinator then tells the server it was cancelled, and the server decides what the task does
/// next. Stopping the task itself is the task's own cancellation, not this.
/// </remarks>
internal sealed class TaskHandbackCall(
    PendingQuestionStore store,
    HandbackCallInfo info,
    string taskId,
    Func<PendingQuestion, CancellationToken, Task> deliver) : IMcpHandbackChannel, IHandbackOwner
{
    private readonly List<PendingEarlierRound> _earlierRounds = [];

    public HandbackCallInfo Info { get; } = info;

    public string? TaskId { get; } = taskId;

    public IReadOnlyList<PendingEarlierRound> EarlierRounds
    {
        get { lock (_earlierRounds) return [.. _earlierRounds]; }
    }

    public async ValueTask<McpHandbackOutcome> AskAsync(McpHandbackQuestion question, CancellationToken ct)
    {
        var (pending, refusal) = await store.OpenAsync(this, question, ct).ConfigureAwait(false);
        if (pending is null)
            return McpHandbackOutcome.Decline(refusal ?? "the question could not be handed back");

        try
        {
            await deliver(pending, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Nobody was told, so nobody will answer: close it now rather than wait out the TTL.
            await store.CancelQuestionAsync(pending.QuestionId, "the question couldn't be delivered").ConfigureAwait(false);
            return McpHandbackOutcome.Decline("the question couldn't be delivered to the calling agent");
        }

        var submission = await pending.Submission.WaitAsync(ct).ConfigureAwait(false);
        if (submission.Target is null)
            return McpHandbackOutcome.Cancel(submission.Outcome.Reason ?? "the question was not answered");

        lock (_earlierRounds)
        {
            _earlierRounds.Add(new PendingEarlierRound
            {
                Message = McpElicitationSchemaDescriber.Flatten(question.Request.Message ?? string.Empty),
                Action = submission.Outcome.Action,
                AnsweredFields = [.. submission.Outcome.Content?.Keys ?? []],
            });
        }

        return submission.Outcome;
    }

    public void Abort(string reason)
    {
        // The question's settlement (a cancel outcome) is all there is to do: the task goes on,
        // and the server hears the question was cancelled.
    }
}
