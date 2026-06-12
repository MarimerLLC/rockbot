namespace RockBot.Agent.McpBridge.ArgGuards;

/// <summary>
/// Stateless pipeline for per-server argument guards: config-time validation (fail
/// closed at server connect) and invoke-time evaluation (first rejection wins).
/// Kept static so the bridge integration is a one-liner and the whole pipeline is
/// unit-testable without a live MCP server.
/// </summary>
public static class McpArgGuardEvaluator
{
    /// <summary>
    /// Validates a server's argGuards config. Returns null when valid; otherwise an
    /// operator-actionable error string (unknown handler, missing handler name, options
    /// validation failure, or guards configured with no registry available). Callers
    /// must fail closed — a server whose declared policy cannot be enforced must not
    /// be connected.
    /// </summary>
    public static string? ValidateConfig(
        IMcpArgGuardRegistry? registry,
        string serverName,
        McpBridgeServerConfig config)
    {
        if (config.ArgGuards.Count == 0)
            return null;

        if (registry is null)
            return $"Server '{serverName}' declares argGuards but no IMcpArgGuardRegistry is registered.";

        foreach (var rule in config.ArgGuards)
        {
            if (string.IsNullOrWhiteSpace(rule.Handler))
                return $"Server '{serverName}' has an argGuards entry with no 'handler' name.";

            if (!registry.Contains(rule.Handler))
                return $"Server '{serverName}' argGuards references unknown handler '{rule.Handler}'. " +
                       $"Known handlers: [{string.Join(", ", registry.KnownHandlers)}].";

            try
            {
                registry.Get(rule.Handler).ValidateOptions(rule.Options);
            }
            catch (Exception ex)
            {
                return $"Server '{serverName}' argGuards handler '{rule.Handler}' rejected its options: {ex.Message}";
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates all matching guard rules for one invocation, in declaration order;
    /// the first rejection short-circuits. Returns the rejection message, or null when
    /// the call may proceed. An unresolvable handler or a guard that throws fails
    /// closed with an explanatory message.
    /// </summary>
    public static async ValueTask<string?> EvaluateAsync(
        IMcpArgGuardRegistry? registry,
        string serverName,
        McpBridgeServerConfig config,
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken ct)
    {
        if (config.ArgGuards.Count == 0)
            return null;

        if (registry is null)
            return $"Tool call blocked: server '{serverName}' declares argGuards but no guard registry " +
                   "is available to enforce them (fail closed).";

        foreach (var rule in config.ArgGuards)
        {
            if (rule.Tools.Count > 0 &&
                !rule.Tools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(rule.Handler) || !registry.Contains(rule.Handler))
            {
                return $"Tool call blocked: server '{serverName}' argGuards references unknown handler " +
                       $"'{rule.Handler}' (fail closed).";
            }

            try
            {
                var context = new McpArgGuardContext(serverName, toolName, arguments, rule.Options);
                var result = await registry.Get(rule.Handler).ApplyAsync(context, ct);
                if (result.IsRejected)
                    return result.RejectionMessage ?? $"Tool call blocked by '{rule.Handler}' guard.";
            }
            catch (Exception ex)
            {
                return $"Tool call blocked: '{rule.Handler}' guard failed to evaluate ({ex.Message}). " +
                       "Failing closed.";
            }
        }

        return null;
    }
}
