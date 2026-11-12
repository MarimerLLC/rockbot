using RockBot.UserProxy.ChatState;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Turns one turn's progress replies and structured events into chat events. Applies them
/// synchronously, on the thread that received them, so nothing posted later can land after the
/// turn's end (a <see cref="Progress{T}"/> posting to the circuit could).
/// </summary>
public sealed class ChatTurnObserver(ChatStateService chatState, string turnId) : ITurnObserver, IProgress<AgentReply>
{
    public void Report(AgentReply reply)
    {
        if (reply.ToolCall is { } toolCall)
            chatState.Apply(new ToolCallReported(MessageCategory.PrimaryProgress, null, toolCall, reply.Content));
        else
            chatState.Apply(new TurnProgress(turnId, reply.Content));
    }

    public void OnRunHeartbeat(RunHeartbeat heartbeat) =>
        chatState.Apply(new TurnHeartbeat(turnId, heartbeat.Status));

    public void OnTaskListSnapshot(TaskListSnapshot snapshot) =>
        chatState.Apply(new TaskListSnapshotReceived(snapshot));

    public void OnTaskListDelta(TaskListDelta delta) =>
        chatState.Apply(new TaskListDeltaReceived(delta));

    /// <summary>Maps how the proxy saw the turn end onto the chat's <see cref="TurnEnded"/>.</summary>
    public static TurnEnded ToTurnEnded(string turnId, TurnResult result) => result.Outcome switch
    {
        TurnResultOutcome.Success => new TurnEnded(turnId, TurnOutcome.Completed) { Reply = result.Reply },
        TurnResultOutcome.TimedOut => new TurnEnded(turnId, TurnOutcome.TimedOut),
        TurnResultOutcome.AgentLost => new TurnEnded(turnId, TurnOutcome.AgentLost) { Error = result.Error },
        TurnResultOutcome.Cancelled => new TurnEnded(turnId, TurnOutcome.Cancelled) { Reply = result.Reply },
        _ => new TurnEnded(turnId, TurnOutcome.AgentError) { Reply = result.Reply, Error = result.Error },
    };
}
