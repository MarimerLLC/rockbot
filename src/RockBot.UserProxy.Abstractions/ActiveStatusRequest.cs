namespace RockBot.UserProxy;

/// <summary>
/// Requests a snapshot of currently active background work (subagents, etc.)
/// so the UI can reconcile its indicators on startup or reconnect.
/// Lightweight request — no LLM invocation, deterministic response.
/// </summary>
public sealed record ActiveStatusRequest
{
    /// <summary>
    /// The session whose processing state <see cref="ActiveStatusResponse.IsProcessing"/> reports.
    /// Null keeps the older behaviour (the Blazor client's fixed session).
    /// </summary>
    public string? SessionId { get; init; }
}
