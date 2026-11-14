namespace RockBot.UserProxy.AgUi;

/// <summary>Words for how a turn ended, shared by the CLI's messages and <c>RUN_ERROR</c> events.</summary>
public static class TurnResultText
{
    /// <summary>The line describing a turn that didn't succeed, or null for one that did.</summary>
    public static string? Describe(TurnResult result) => result.Outcome switch
    {
        TurnResultOutcome.Success => null,
        TurnResultOutcome.Cancelled => "the agent cancelled the turn",
        TurnResultOutcome.Error => $"the agent reported an error{(result.Error is { Length: > 0 } e ? $": {e}" : "")}",
        TurnResultOutcome.AgentLost => $"lost contact with the agent{(result.Error is { Length: > 0 } e ? $" ({e})" : "")}",
        _ => "no reply received (timeout)",
    };
}
