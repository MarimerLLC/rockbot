namespace RockBot.A2A;

/// <summary>
/// Trust level assigned to an external agent caller. Each caller progresses
/// through these levels independently based on user approval.
/// </summary>
public enum AgentTrustLevel
{
    /// <summary>Read-only access; summarize request and notify user.</summary>
    Observe = 1,

    /// <summary>Same as Observe, but system observes user responses and proposes skill drafts.</summary>
    Learn = 2,

    /// <summary>System has candidate skills and asks user to approve them.</summary>
    Propose = 3,

    /// <summary>Approved skills execute autonomously; results reported to user post-hoc.</summary>
    Act = 4
}
