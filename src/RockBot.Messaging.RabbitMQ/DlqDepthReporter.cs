using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// Hosted service that periodically polls DLQ depths via the RabbitMQ Management API
/// and updates the <see cref="RabbitMqDiagnostics.DlqDepths"/> dictionary so the
/// <c>rockbot.messaging.dlq.depth</c> gauge reflects current queue backlogs.
/// Silently exits if the Management API is not configured.
/// </summary>
internal sealed class DlqDepthReporter : IHostedService, IDisposable
{
    private readonly RabbitMqManagementClient _client;
    private readonly ILogger<DlqDepthReporter> _logger;
    private Timer? _timer;

    public DlqDepthReporter(
        RabbitMqManagementClient client,
        ILogger<DlqDepthReporter> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsEnabled)
        {
            _logger.LogDebug("DlqDepthReporter: Management API not configured; DLQ depth gauge disabled");
            return Task.CompletedTask;
        }

        // Poll immediately, then every 60 seconds
        _timer = new Timer(
            state => { _ = PollAsync(); },
            null,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromSeconds(60));

        _logger.LogInformation("DlqDepthReporter: started — polling DLQ depths every 60 s");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private async Task PollAsync()
    {
        try
        {
            var queues = await _client.GetDlqQueuesAsync();
            var knownNames = new HashSet<string>(queues.Select(q => q.Name), StringComparer.Ordinal);

            foreach (var q in queues)
                RabbitMqDiagnostics.DlqDepths[q.Name] = q.MessageCount;

            // Remove entries for queues no longer reported by the Management API
            foreach (var stale in RabbitMqDiagnostics.DlqDepths.Keys
                         .Where(k => !knownNames.Contains(k))
                         .ToList())
            {
                RabbitMqDiagnostics.DlqDepths.TryRemove(stale, out _);
            }

            if (queues.Any(q => q.MessageCount > 0))
                _logger.LogDebug(
                    "DlqDepthReporter: {Queues} DLQ(s) have messages: {Details}",
                    queues.Count(q => q.MessageCount > 0),
                    string.Join(", ", queues.Where(q => q.MessageCount > 0)
                        .Select(q => $"{q.Name}={q.MessageCount}")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DlqDepthReporter: poll failed");
        }
    }
}
