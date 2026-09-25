using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Picks the <see cref="IMcpElicitationResponder"/> that answers for one MCP server.
/// </summary>
/// <remarks>
/// Who answers is per server because servers ask different kinds of question. A mail server's
/// "which mailbox?" is transcription from the call's own arguments; a research server's "which
/// of these meanings did you intend?" needs the conversation. Named responders are ordinary
/// keyed services, so a host adds one with <c>AddKeyedSingleton&lt;IMcpElicitationResponder, T&gt;(key)</c>
/// and a server opts in with <see cref="McpElicitationConfig.Responder"/>.
/// </remarks>
public static class McpElicitationResponders
{
    /// <summary>
    /// Returns the responder <paramref name="config"/> names, the host's
    /// <paramref name="defaultResponder"/> when it names none, or null when the named responder
    /// is not registered.
    /// </summary>
    public static IMcpElicitationResponder? Resolve(
        McpElicitationConfig? config,
        IMcpElicitationResponder? defaultResponder,
        IServiceProvider? services,
        string serverName,
        ILogger logger)
    {
        var key = config?.Responder?.Trim();
        if (string.IsNullOrEmpty(key))
            return defaultResponder;

        // Keys match as registered, then lower-cased — "LLM" finds "llm", as mode names are
        // case-insensitive. Register keys in lower case for that to hold.
        var named = services?.GetKeyedService<IMcpElicitationResponder>(key)
                    ?? services?.GetKeyedService<IMcpElicitationResponder>(key.ToLowerInvariant());
        if (named is null)
        {
            // Fail closed: answering with a responder the operator did not choose would widen
            // what the bridge says on a user's behalf. Defaults still apply.
            logger.LogWarning(
                "MCP server {Server} names elicitation responder '{Responder}', which is not registered; " +
                "only configured defaults will be answered",
                serverName, key);
        }

        return named;
    }
}
