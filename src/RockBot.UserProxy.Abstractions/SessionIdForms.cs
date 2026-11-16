namespace RockBot.UserProxy;

/// <summary>
/// The two forms a session id takes on the wire: the raw id, and the agent's working-memory form
/// <c>session/&lt;raw id&gt;</c>, which a subagent's or worker's tool progress keeps.
/// </summary>
public static class SessionIdForms
{
    /// <summary>The prefix of the working-memory form.</summary>
    public const string NamespacedPrefix = "session/";

    /// <summary>The raw session id: <paramref name="sessionId"/> with one <c>session/</c> prefix removed.</summary>
    public static string Raw(string sessionId) =>
        sessionId.StartsWith(NamespacedPrefix, StringComparison.Ordinal) ? sessionId[NamespacedPrefix.Length..] : sessionId;

    /// <summary>True when the two ids name the same session, in either form.</summary>
    public static bool AreSame(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Raw(a), Raw(b), StringComparison.Ordinal);
}
