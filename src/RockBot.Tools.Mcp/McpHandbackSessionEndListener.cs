using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Tools.Mcp;

/// <summary>
/// When a subagent's session ends, asks the bridge to release any MCP question it handed back
/// to that session and the run never answered. Without this the parked call would wait out its
/// TTL for a run that no longer exists. Sends nothing while no connected server hands back.
/// </summary>
public sealed class McpHandbackSessionEndListener(
    McpServerIndex index,
    IMessagePublisher publisher,
    AgentIdentity identity) : ISessionEndListener
{
    public Task OnSessionEndedAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId) || !index.Servers.Any(s => s.Handback))
            return Task.CompletedTask;

        var envelope = new McpReleaseSessionQuestionsRequest { SessionId = sessionId }
            .ToEnvelope(source: identity.Name);
        return publisher.PublishAsync(McpManagementExecutor.ManageTopic, envelope, ct);
    }
}
