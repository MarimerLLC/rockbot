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
    private const string SystemRole = "system";

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
                        // Each with the URL of its bytes, as in rockbot.attachments.
                        Attachments = turn.Attachments is { Count: > 0 } attachments
                            ? attachments.Select(a => new { a.Mime, a.Path, a.FileName, Url = AttachmentLinks.Url(a) }).ToList()
                            : null,
                    },
                },
                TurnEventMapper.PayloadJson);

            // History holds the user's turns, the agent's, and system turns: prompts the agent
            // wrote to itself (a consolidation after a subagent finished, a feedback note). Those
            // keep their role, so a client leaves them out, as the Blazor UI does; shown as the
            // agent's they would read as replies it never gave. Anything else is the agent's.
            messages.Add(turn.Role.ToLowerInvariant() switch
            {
                UserRole => new AGUIUserMessage { Id = id, Content = turn.Content, Metadata = metadata },
                SystemRole => new AGUISystemMessage { Id = id, Content = turn.Content, Metadata = metadata },
                _ => new AGUIAssistantMessage { Id = id, Content = turn.Content, Name = turn.AgentName, Metadata = metadata },
            });
        }

        return new MessagesSnapshotEvent
        {
            Messages = messages,
            Timestamp = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds(),
        };
    }
}
