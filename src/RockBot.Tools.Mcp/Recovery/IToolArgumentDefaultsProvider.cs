namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Resolves a default value for a missing required tool argument.
/// Implementations are queried in registration order; the first one whose
/// <see cref="CanResolve"/> returns <c>true</c> wins. See
/// <c>design/self-repair.md</c> Phase 1, Stage A.
/// </summary>
public interface IToolArgumentDefaultsProvider
{
    /// <summary>
    /// Cheap predicate. Returns <c>true</c> if this provider can produce a value
    /// for the given (server, tool, field). Must not perform I/O.
    /// </summary>
    bool CanResolve(string serverName, string toolName, string fieldName);

    /// <summary>
    /// Produces a value (or null if resolution unexpectedly fails). May perform I/O.
    /// </summary>
    Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct);
}

/// <summary>
/// Inputs to <see cref="IToolArgumentDefaultsProvider.ResolveAsync"/>.
/// </summary>
public sealed record ResolveContext(
    string ServerName,
    string ToolName,
    string FieldName,
    IReadOnlyDictionary<string, object?> ExistingArgs);

/// <summary>
/// Output of <see cref="IToolArgumentDefaultsProvider.ResolveAsync"/>.
/// When <see cref="RequiresFanOut"/> is <c>true</c>, <see cref="Value"/> must be
/// an <see cref="System.Collections.IEnumerable"/> of values; the recovery executor
/// will issue one tool call per element and aggregate the responses.
/// </summary>
public sealed record ResolvedDefault(object? Value, bool RequiresFanOut = false);

/// <summary>
/// Delegate over <see cref="McpToolProxy.ExecuteAsync(ToolInvokeRequest, IReadOnlyDictionary{string, string}?, CancellationToken)"/>.
/// Lets recovery code and providers issue MCP calls without taking a hard dependency
/// on the sealed proxy type, which simplifies unit testing.
/// </summary>
public delegate Task<ToolInvokeResponse> McpInvokeDelegate(
    ToolInvokeRequest request,
    IReadOnlyDictionary<string, string>? extraHeaders,
    CancellationToken ct);
