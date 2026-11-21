namespace RockBot.Host;

/// <summary>Well-known working-memory categories that readers outside their writer treat specially.</summary>
public static class WorkingMemoryCategories
{
    /// <summary>
    /// What a resource watch delivered to a session: a change's content, or the watch's ending or
    /// expiry warning, which quotes the user's instructions for it (#853). In a subagent's namespace
    /// these are for that run alone (#854): they are not outputs to relay to the user, and they stay
    /// out of what the dream learns from.
    /// </summary>
    public const string McpWatch = "mcp-watch";

    /// <summary>True for an entry a resource watch delivered (<see cref="McpWatch"/>).</summary>
    public static bool IsWatchDelivery(string? category) =>
        string.Equals(category, McpWatch, StringComparison.Ordinal);
}
