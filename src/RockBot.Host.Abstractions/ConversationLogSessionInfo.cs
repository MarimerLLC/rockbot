namespace RockBot.Host;

/// <summary>
/// Summary of one session's presence in the conversation log. Used to discover which
/// sessions are recallable without reading their turns.
/// </summary>
/// <param name="SessionId">The session this summary describes.</param>
/// <param name="TurnCount">How many logged turns the session has.</param>
/// <param name="FirstTimestamp">Timestamp of the session's earliest logged turn.</param>
/// <param name="LastTimestamp">Timestamp of the session's most recent logged turn.</param>
public sealed record ConversationLogSessionInfo(
    string SessionId,
    int TurnCount,
    DateTimeOffset FirstTimestamp,
    DateTimeOffset LastTimestamp);
