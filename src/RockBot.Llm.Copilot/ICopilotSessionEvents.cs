namespace RockBot.Llm.Copilot;

/// <summary>
/// Callback interface for Copilot session events. Allows the host to receive
/// tool-call progress notifications without the Copilot project referencing
/// RockBot.Host. Wire this to <c>IToolProgressNotifier</c> in DI.
/// </summary>
public interface ICopilotSessionEvents
{
    /// <summary>Called when the Copilot session starts executing a tool.</summary>
    Task OnToolStartAsync(string toolName, string? arguments, CancellationToken ct);

    /// <summary>Called when a tool execution completes within the Copilot session.</summary>
    Task OnToolCompleteAsync(string toolName, bool success, string? resultSummary, CancellationToken ct);
}
