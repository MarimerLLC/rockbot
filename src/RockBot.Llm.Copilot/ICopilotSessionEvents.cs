namespace RockBot.Llm.Copilot;

/// <summary>
/// Callback interface for Copilot session events. Allows the host to receive
/// tool-call progress notifications without the Copilot project referencing
/// RockBot.Host. Wire this to <c>IToolProgressNotifier</c> in DI.
/// </summary>
public interface ICopilotSessionEvents
{
    /// <summary>Called when the Copilot session starts executing a tool.</summary>
    /// <param name="toolCallId">The SDK's id for the call; the same id arrives with its completion.</param>
    Task OnToolStartAsync(string toolCallId, string toolName, string? arguments, CancellationToken ct);

    /// <summary>Called when a tool execution completes within the Copilot session.</summary>
    /// <param name="toolCallId">The id the call started with.</param>
    Task OnToolCompleteAsync(string toolCallId, bool success, string? resultSummary, CancellationToken ct);
}
