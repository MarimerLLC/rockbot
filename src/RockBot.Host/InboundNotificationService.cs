using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Background service that monitors the notification queue and presents batched
/// A2A notifications to the user when they become idle (~2 minutes of inactivity).
/// </summary>
internal sealed class InboundNotificationService : IHostedService, IDisposable
{
    /// <summary>How long the user must be idle before flushing notifications.</summary>
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(2);

    /// <summary>How often we check for idle state.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Session ID used for A2A inbound notifications in the UI.</summary>
    internal const string A2AInboundSessionId = "a2a-inbound";

    /// <summary>Agent name used for A2A inbound notifications in the UI.</summary>
    internal const string A2AInboundAgentName = "A2A-Inbox";

    private readonly IInboundNotificationQueue _queue;
    private readonly IUserActivityMonitor _userActivityMonitor;
    private readonly ISessionTracker _sessionTracker;
    private readonly IMessagePublisher _publisher;
    private readonly AgentIdentity _agent;
    private readonly ILogger<InboundNotificationService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;

    public InboundNotificationService(
        IInboundNotificationQueue queue,
        IUserActivityMonitor userActivityMonitor,
        ISessionTracker sessionTracker,
        IMessagePublisher publisher,
        AgentIdentity agent,
        ILogger<InboundNotificationService> logger)
    {
        _queue = queue;
        _userActivityMonitor = userActivityMonitor;
        _sessionTracker = sessionTracker;
        _publisher = publisher;
        _agent = agent;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(CheckAndFlush, null, PollInterval, PollInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Dispose();
    }

    private async void CheckAndFlush(object? state)
    {
        try
        {
            if (_queue.PendingCount == 0)
                return;

            // User is idle when: no active session loop AND no recent activity
            var hasActiveLoop = _sessionTracker.HasActiveUserLoop(WellKnownSessions.Primary);
            var isRecentlyActive = _userActivityMonitor.IsUserActive(IdleThreshold);

            if (hasActiveLoop || isRecentlyActive)
                return;

            var ct = _cts.Token;
            var notifications = await _queue.DrainAsync(ct);
            if (notifications.Count == 0)
                return;

            _logger.LogInformation(
                "Flushing {Count} A2A notification(s) to user after idle period",
                notifications.Count);

            var content = FormatNotifications(notifications);

            var reply = new AgentReply
            {
                Content = content,
                SessionId = A2AInboundSessionId,
                AgentName = A2AInboundAgentName,
                IsFinal = true
            };

            var envelope = reply.ToEnvelope<AgentReply>(source: _agent.Name);
            await _publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{_agent.Name}", envelope, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing A2A notifications");
        }
    }

    private static string FormatNotifications(IReadOnlyList<InboundNotification> notifications)
    {
        if (notifications.Count == 1)
        {
            var n = notifications[0];
            return $"**Agent \"{n.CallerName}\" reached out** (skill: {n.SkillId ?? "general"})\n\n{n.Summary}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"**While you were away, {notifications.Count} agents reached out:**\n");
        for (var i = 0; i < notifications.Count; i++)
        {
            var n = notifications[i];
            sb.AppendLine($"{i + 1}. **{n.CallerName}** (skill: {n.SkillId ?? "general"}) — {n.Summary}");
        }
        return sb.ToString().TrimEnd();
    }
}
