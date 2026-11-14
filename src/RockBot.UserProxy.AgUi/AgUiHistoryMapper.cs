using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// Maps a session's conversation history to an AG-UI <c>MESSAGES_SNAPSHOT</c>, the body of
/// <c>GET /threads/{id}/messages</c>.
/// </summary>
internal static class AgUiHistoryMapper
{
    private const string UserRole = "user";

    /// <summary>
    /// One message per turn, in order. A turn's id is <c>{threadId}-{index}</c>: history has no
    /// ids of its own, and a position is stable for as long as the context isn't cleared. What
    /// AG-UI has no field for (RockBot's timestamp, the agent's name, attachments) is under
    /// <c>metadata.rockbot</c>.
    /// </summary>
    public static MessagesSnapshotEvent ToSnapshot(string threadId, ConversationHistoryResponse history, TimeProvider? clock = null)
    {
        var messages = new List<AGUIMessage>(history.Turns.Count);
        for (var i = 0; i < history.Turns.Count; i++)
        {
            var turn = history.Turns[i];
            var id = $"{threadId}-{i}";
            var metadata = JsonSerializer.SerializeToElement(
                new
                {
                    rockbot = new
                    {
                        turn.Timestamp,
                        turn.AgentName,
                        Attachments = turn.Attachments is { Count: > 0 } ? turn.Attachments : null,
                    },
                },
                TurnEventMapper.PayloadJson);

            // History holds user turns and the agent's; anything that isn't the user's is shown
            // as the agent's.
            messages.Add(string.Equals(turn.Role, UserRole, StringComparison.OrdinalIgnoreCase)
                ? new AGUIUserMessage { Id = id, Content = turn.Content, Metadata = metadata }
                : new AGUIAssistantMessage { Id = id, Content = turn.Content, Name = turn.AgentName, Metadata = metadata });
        }

        return new MessagesSnapshotEvent
        {
            Messages = messages,
            Timestamp = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds(),
        };
    }
}
