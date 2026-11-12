using Microsoft.Extensions.Logging;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// The run lifecycle of one user turn (#795): <see cref="RunStarted"/>, a
/// <see cref="RunHeartbeat"/> every interval carrying the latest status text, and exactly one
/// <see cref="RunFinished"/>. Publishes only when the proxy advertised
/// <see cref="ClientCapabilities.AgentEvents"/> and the turn has a correlation id; otherwise every
/// method is a no-op. Disposing a run nothing finished reports it as an error, so a code path
/// that forgets to finish it still ends the proxy's wait.
/// </summary>
public sealed class TurnLifecycle : IAsyncDisposable
{
    private readonly IMessagePublisher _publisher;
    private readonly string _agentName;
    private readonly string _threadId;
    private readonly string? _correlationId;
    private readonly string _replyTo;
    private readonly TimeSpan _heartbeatInterval;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _heartbeatCts = new();
    private long _startedAt;
    private Task? _heartbeatLoop;
    private volatile string? _status;
    private int _started;
    private int _finished;

    public TurnLifecycle(
        IMessagePublisher publisher,
        string agentName,
        string threadId,
        string? correlationId,
        string replyTo,
        bool eventsEnabled,
        TimeSpan heartbeatInterval,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _publisher = publisher;
        _agentName = agentName;
        _threadId = threadId;
        _correlationId = correlationId;
        _replyTo = replyTo;
        _heartbeatInterval = heartbeatInterval > TimeSpan.Zero ? heartbeatInterval : TimeSpan.FromSeconds(15);
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        Enabled = eventsEnabled && !string.IsNullOrEmpty(correlationId);
    }

    /// <summary>True when this turn publishes run events.</summary>
    public bool Enabled { get; }

    /// <summary>True once <see cref="FinishAsync"/> has run.</summary>
    public bool IsFinished => Volatile.Read(ref _finished) != 0;

    /// <summary>The latest status text, carried on the next heartbeat.</summary>
    public string? Status => _status;

    /// <summary>Publishes <see cref="RunStarted"/> and starts the heartbeat. Only the first call does anything.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Enabled || Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _startedAt = _time.GetTimestamp();
        await PublishAsync(new RunStarted
        {
            ThreadId = _threadId,
            RunId = _correlationId!,
            AgentName = _agentName,
            HeartbeatIntervalSeconds = (int)Math.Ceiling(_heartbeatInterval.TotalSeconds),
            Timestamp = _time.GetUtcNow(),
        }, ct);

        _heartbeatLoop = HeartbeatLoopAsync(_heartbeatCts.Token);
    }

    /// <summary>Records the latest progress text (a tool call, a stage) for the next heartbeat.</summary>
    public void UpdateStatus(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _status = text;
    }

    /// <summary>
    /// Stops the heartbeat and publishes <see cref="RunFinished"/>. Only the first call does
    /// anything, so every exit path of a turn can call it.
    /// </summary>
    public async Task FinishAsync(string outcome, string? error = null)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return;

        await _heartbeatCts.CancelAsync();
        if (_heartbeatLoop is not null)
        {
            try { await _heartbeatLoop; }
            catch (OperationCanceledException) { }
        }

        if (!Enabled || Volatile.Read(ref _started) == 0)
            return;

        await PublishAsync(new RunFinished
        {
            ThreadId = _threadId,
            RunId = _correlationId!,
            Outcome = outcome,
            Error = error,
            Timestamp = _time.GetUtcNow(),
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await FinishAsync(RunOutcome.Error, "The run ended without reporting an outcome.");
        _heartbeatCts.Dispose();
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await PublishAsync(new RunHeartbeat
                {
                    ThreadId = _threadId,
                    RunId = _correlationId!,
                    Timestamp = _time.GetUtcNow(),
                    ElapsedSeconds = Math.Round(_time.GetElapsedTime(_startedAt).TotalSeconds, 1),
                    Status = _status,
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Finished.
        }
    }

    private async Task PublishAsync<T>(T evt, CancellationToken ct) where T : class
    {
        try
        {
            var envelope = evt.ToEnvelope<T>(source: _agentName, correlationId: _correlationId);
            await _publisher.PublishAsync(_replyTo, envelope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish {Type} for run {RunId}", typeof(T).Name, _correlationId);
        }
    }
}
