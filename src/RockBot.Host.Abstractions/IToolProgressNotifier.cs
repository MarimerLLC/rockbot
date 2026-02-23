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
}
