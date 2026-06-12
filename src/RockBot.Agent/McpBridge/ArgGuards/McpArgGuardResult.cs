namespace RockBot.Agent.McpBridge.ArgGuards;

/// <summary>
/// Outcome of a guard evaluation. Rejection messages are returned verbatim to the LLM
/// as the tool result, so they should name the offending argument and explain how to
/// retry correctly.
/// </summary>
public sealed record McpArgGuardResult
{
    public static readonly McpArgGuardResult Allowed = new() { IsRejected = false };

    public static McpArgGuardResult Reject(string message) =>
        new() { IsRejected = true, RejectionMessage = message };

    public bool IsRejected { get; init; }

    public string? RejectionMessage { get; init; }
}
