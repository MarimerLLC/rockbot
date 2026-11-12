using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Notifies listeners about tool invocation progress during an LLM tool-calling loop.
/// Implementations can publish progress messages to the user (via RabbitMQ), log, or no-op.
/// </summary>
public interface IToolProgressNotifier
{
    /// <summary>Called before a tool is invoked.</summary>
    Task OnToolInvokingAsync(string toolName, string? argsSummary, CancellationToken ct);

    /// <summary>Called after a tool completes.</summary>
    Task OnToolInvokedAsync(string toolName, string? resultSummary, CancellationToken ct);

    /// <summary>
    /// Called when a tool call starts, finishes or fails, with its call id, duration and outcome.
    /// The default maps onto <see cref="OnToolInvokingAsync"/> and <see cref="OnToolInvokedAsync"/>
    /// for implementations that only know those.
    /// </summary>
    Task OnToolCallAsync(ToolCallInfo info, CancellationToken ct) => info.Phase switch
    {
        ToolCallPhase.Started => OnToolInvokingAsync(info.ToolCallName, info.ArgsSummary, ct),
        ToolCallPhase.Failed => OnToolInvokedAsync(info.ToolCallName, $"failed: {info.Error}", ct),
        _ => OnToolInvokedAsync(info.ToolCallName, info.Result, ct),
    };
}
