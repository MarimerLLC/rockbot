namespace RockBot.Subagent;

/// <summary>
/// Configuration options for the subagent subsystem.
/// </summary>
public sealed class SubagentOptions
{
    public int MaxConcurrentSubagents { get; set; } = 3;
    public int DefaultTimeoutMinutes { get; set; } = 10;
}
