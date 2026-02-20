using System.Collections.Concurrent;
using RockBot.Messaging;
using RockBot.Scripts;

namespace RockBot.Scripts.Remote;

/// <summary>
/// Implements <see cref="IScriptRunner"/> by publishing script requests to the message bus
/// and awaiting responses on a per-agent reply topic.
/// </summary>
internal sealed class MessageBusScriptRunner(
    IMessagePublisher publisher,
    string agentName) : IScriptRunner
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ScriptInvokeResponse>> _pending = new();

    public string ReplyTopic => $"script.result.{agentName}";

    public async Task<ScriptInvokeResponse> ExecuteAsync(ScriptInvokeRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ScriptInvokeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.ToolCallId] = tcs;

        try
        {
            var envelope = request.ToEnvelope<ScriptInvokeRequest>(
                source: agentName,
                replyTo: ReplyTopic);

            await publisher.PublishAsync("script.invoke", envelope, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds + 10));

            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _pending.TryRemove(request.ToolCallId, out _);
            throw;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(request.ToolCallId, out _);
            return new ScriptInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                Stderr = $"Timed out waiting for script result after {request.TimeoutSeconds + 10}s",
                ExitCode = -1,
                ElapsedMs = 0
            };
        }
    }

    /// <summary>
    /// Called by <see cref="ScriptResultSubscriber"/> when a result arrives on the reply topic.
    /// Resolves the matching pending <see cref="TaskCompletionSource{T}"/>.
    /// </summary>
    public void CompleteRequest(ScriptInvokeResponse response)
    {
        if (_pending.TryRemove(response.ToolCallId, out var tcs))
            tcs.TrySetResult(response);
    }
}
