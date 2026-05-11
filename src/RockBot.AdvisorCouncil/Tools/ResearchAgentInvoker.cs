using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.A2A;
using RockBot.AdvisorCouncil.Council;
using RockBot.Messaging;

namespace RockBot.AdvisorCouncil.Tools;

/// <summary>
/// Synchronous A2A invocation of <c>ResearchAgent</c> from inside the council pod.
/// Maintains a process-unique reply queue and matches incoming results to outstanding
/// invocations via correlation id. The existing <c>InvokeAgentExecutor</c> is async /
/// fire-and-forget against the primary agent's bus and is not reusable for the
/// synchronous wait the council needs inside a persona branch.
/// </summary>
internal sealed class ResearchAgentInvoker : IHostedService, IAsyncDisposable
{
    private const string ResearchTaskTopic = "agent.task.ResearchAgent";

    private readonly IMessagePublisher _publisher;
    private readonly IMessageSubscriber _subscriber;
    private readonly CouncilOptions _options;
    private readonly ILogger<ResearchAgentInvoker> _logger;

    private readonly string _replyTopic =
        $"council.research-reply.{Environment.ProcessId}.{Guid.NewGuid():N}".ToLowerInvariant();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new(StringComparer.Ordinal);
    private ISubscription? _subscription;

    public ResearchAgentInvoker(
        IMessagePublisher publisher,
        IMessageSubscriber subscriber,
        IOptions<CouncilOptions> options,
        ILogger<ResearchAgentInvoker> logger)
    {
        _publisher = publisher;
        _subscriber = subscriber;
        _options = options.Value;
        _logger = logger;
        Function = new InvokerFunction(this);
    }

    public AIFunction Function { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await _subscriber.SubscribeAsync(
            topic: _replyTopic,
            subscriptionName: _replyTopic,
            handler: HandleReplyAsync,
            cancellationToken: cancellationToken);
        _logger.LogInformation("ResearchAgentInvoker reply queue {Topic} ready", _replyTopic);
    }

    private Task<MessageResult> HandleReplyAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        var correlationId = envelope.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            _logger.LogWarning("Received research reply without correlation id; type={Type}", envelope.MessageType);
            return Task.FromResult(MessageResult.Ack);
        }

        if (!_pending.TryRemove(correlationId, out var tcs))
        {
            _logger.LogDebug("No pending research call for correlationId={CorrelationId}", correlationId);
            return Task.FromResult(MessageResult.Ack);
        }

        try
        {
            if (envelope.MessageType.Contains("AgentTaskError", StringComparison.Ordinal))
            {
                var err = envelope.GetPayload<AgentTaskError>();
                tcs.TrySetResult($"(research failed: {err?.Message ?? "unknown"})");
            }
            else
            {
                var result = envelope.GetPayload<AgentTaskResult>();
                var text = result?.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text;
                tcs.TrySetResult(string.IsNullOrWhiteSpace(text) ? "(no research result)" : text);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize research reply for {CorrelationId}", correlationId);
            tcs.TrySetResult("(research reply could not be parsed)");
        }

        return Task.FromResult(MessageResult.Ack);
    }

    public async Task<string> InvokeAsync(string question, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "(empty research question)";

        var taskId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[taskId] = tcs;

        try
        {
            var request = new AgentTaskRequest
            {
                TaskId = taskId,
                Skill = "research",
                Message = new AgentMessage
                {
                    Role = "user",
                    Parts = [new AgentMessagePart { Kind = "text", Text = question }]
                }
            };

            var envelope = request.ToEnvelope<AgentTaskRequest>(
                source: "AdvisorCouncil",
                correlationId: taskId,
                replyTo: _replyTopic);

            await _publisher.PublishAsync(ResearchTaskTopic, envelope, ct);

            var timeout = TimeSpan.FromSeconds(Math.Max(5, _options.ResearchAgentTimeoutSeconds));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var sw = Stopwatch.StartNew();
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, timeoutCts.Token));
            sw.Stop();

            if (completed == tcs.Task)
            {
                _logger.LogInformation("Research call {TaskId} returned in {Ms}ms", taskId, sw.ElapsedMilliseconds);
                return await tcs.Task;
            }

            _pending.TryRemove(taskId, out _);
            _logger.LogWarning("Research call {TaskId} timed out after {Sec}s", taskId, timeout.TotalSeconds);
            return "(research timed out)";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _pending.TryRemove(taskId, out _);
            throw;
        }
        catch (Exception ex)
        {
            _pending.TryRemove(taskId, out _);
            _logger.LogError(ex, "Research call {TaskId} failed", taskId);
            return $"(research call failed: {ex.Message})";
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
            _subscription = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private sealed class InvokerFunction(ResearchAgentInvoker owner) : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{"type":"object","properties":{"question":{"type":"string","description":"The research question."}},"required":["question"]}""")
            .RootElement;

        public override string Name => "research";

        public override string Description =>
            "Search the web and summarise findings on a topic. Use sparingly — each call is " +
            "delegated to a separate research agent and costs latency. Pass a focused question.";

        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            string? question = null;
            if (arguments.TryGetValue("question", out var v))
                question = v?.ToString();
            if (string.IsNullOrWhiteSpace(question))
                return "Error: missing required argument 'question'.";

            return await owner.InvokeAsync(question!, cancellationToken);
        }
    }
}

