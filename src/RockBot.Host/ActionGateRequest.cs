using System.Collections.Concurrent;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// The user message behind a run that the consequential-action gate (#685) judges, plus the
/// decisions already made for it. Attached to <see cref="ActionGateScope.Request"/> by
/// <see cref="AgentLoopRunner.RunAsync"/>; subagents spawned from the run share the same instance,
/// so a change the user asked for is classified once per turn, not once per subagent.
/// </summary>
/// <param name="UserRequest">The user message the run serves.</param>
/// <param name="PreviousAgentMessage">What the agent said just before it, when known — a reply to a
/// proposal ("mark them complete now") only makes sense next to the proposal.</param>
public sealed record ActionGateRequest(string? UserRequest, string? PreviousAgentMessage)
{
    /// <summary>Decisions by effective tool name. True when the user asked for the change.</summary>
    internal ConcurrentDictionary<string, Task<bool>> Decisions { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Two requests are the same request only when they are the same instance: the decision cache is
    // part of the identity, and ActionGateScope equality must not merge two turns' caches.
    /// <inheritdoc />
    public bool Equals(ActionGateRequest? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>What the gate asks the decision model about one consequential call.</summary>
/// <param name="UserRequest">The user message the run serves.</param>
/// <param name="PreviousAgentMessage">The agent message just before it, if any.</param>
/// <param name="ToolName">The tool as reported (<see cref="ConsequentialActions.DisplayName"/>).</param>
/// <param name="Target">The system the call would change (<see cref="ConsequentialActions.DescribeTarget"/>).</param>
/// <param name="Arguments">The call's arguments, possibly truncated.</param>
public sealed record UserIntentQuery(
    string? UserRequest, string? PreviousAgentMessage, string ToolName, string Target, string? Arguments)
{
    internal const string QuestionName = "user_asked";
    private const int MaxFieldChars = 4000;
    private const int MaxArgumentChars = 600;

    /// <summary>
    /// The decision request: the conversation and proposed call as state, one noul question. Probed
    /// 2026-10-13 against Microsoft-Decision-1: requests scored 0.75–0.9995, non-requests ≤ 0.007.
    /// </summary>
    public DecisionRequest ToDecisionRequest()
    {
        var state = new Dictionary<string, object?>
        {
            ["user_message"] = Truncate(UserRequest ?? "", MaxFieldChars),
        };
        if (!string.IsNullOrWhiteSpace(PreviousAgentMessage))
            state["agent_message_before_it"] = Truncate(PreviousAgentMessage, MaxFieldChars);
        state["proposed_action"] = new Dictionary<string, object?>
        {
            ["tool"] = ToolName,
            ["system_changed"] = Target,
            ["arguments"] = Truncate(Arguments ?? "", MaxArgumentChars),
        };

        return DecisionRequest.Single(DecisionPurposes.ActionGate, state, QuestionName, new NoulQuestion(
            "An AI assistant is about to run proposed_action, which changes an external system. " +
            "Did user_message ask the assistant to make this change, ask for work that requires it, " +
            "or agree to it after agent_message_before_it proposed it?",
            WhenTrue: "The user asked for this change, directly or indirectly (for example \"these " +
                      "should have been marked done\"), or accepted the assistant's offer to make it",
            WhenFalse: "The user only shared information, asked a question, or asked for something " +
                       "else; making this change would be the assistant's own idea"));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
