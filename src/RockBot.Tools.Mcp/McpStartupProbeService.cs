using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Sends a <see cref="McpMetadataRefreshRequest"/> to the MCP Bridge once the agent
/// is fully started (all subscriptions active). This closes the race where the bridge
/// publishes tool-availability messages before the agent has subscribed to its topic.
/// </summary>
internal sealed class McpStartupProbeService : IHostedService
{
    private readonly IMessagePublisher _publisher;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly AgentIdentity _identity;
    private readonly ILogger<McpStartupProbeService> _logger;

    public McpStartupProbeService(
        IMessagePublisher publisher,
        IHostApplicationLifetime lifetime,
        AgentIdentity identity,
        ILogger<McpStartupProbeService> logger)
    {
        _publisher = publisher;
        _lifetime = lifetime;
        _identity = identity;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(() => _ = PublishRefreshAsync());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task PublishRefreshAsync()
    {
        try
        {
            _logger.LogInformation("Requesting MCP tool discovery refresh from bridge");
            var request = new McpMetadataRefreshRequest(); // null ServerName = refresh all
            var envelope = request.ToEnvelope(source: _identity.Name);
            await _publisher.PublishAsync("tool.meta.mcp.refresh", envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send MCP startup refresh request");
        }
    }
}
