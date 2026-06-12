using System.Text.Json;

namespace RockBot.Agent.McpBridge.ArgGuards;

/// <summary>
/// A named, per-server argument validator applied by the bridge before a tool call is
/// forwarded to an MCP server. Implementations are registered in DI under a handler name
/// and resolved via <see cref="IMcpArgGuardRegistry"/> — mcp.json names handlers, never
/// CLR types. (mcp.json is LLM-writable via register_mcp_server, so config-driven type
/// loading would be an arbitrary-code-execution channel; see design/mcp-arg-guards.md.)
/// </summary>
public interface IMcpArgGuard
{
    /// <summary>
    /// Validates the guard's options block at config-load/connect time. Throws
    /// <see cref="InvalidOperationException"/> with an operator-actionable message when
    /// the options are missing or malformed. Called per config entry, not per invoke.
    /// </summary>
    void ValidateOptions(JsonElement? options);

    /// <summary>
    /// Evaluates one tool invocation. <see cref="McpArgGuardContext.Arguments"/> is the
    /// live dictionary the bridge will forward — handlers may mutate it in place (the
    /// AttachmentGateway precedent), though the built-in handlers only inspect it.
    /// </summary>
    ValueTask<McpArgGuardResult> ApplyAsync(McpArgGuardContext context, CancellationToken ct);
}
