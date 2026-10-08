namespace RockBot.Tools.Mcp;

/// <summary>
/// <see cref="IMcpToolDirectory"/> over the wrapper catalog (typed names, registrations, executor)
/// and the server index (which servers and tools exist, and the bridge's per-tool fingerprints).
/// </summary>
internal sealed class McpToolDirectory(McpWrapperCatalog catalog, McpServerIndex index) : IMcpToolDirectory
{
    public IToolExecutor WrapperExecutor => catalog.Executor;

    public McpToolEntry? Resolve(string? serverName, string tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return null;

        if (string.IsNullOrWhiteSpace(serverName))
        {
            return catalog.TryGet(tool, out var byName)
                ? new McpToolEntry(byName.ServerName, byName.ToolName, McpWrapperCatalog.RegistrationFor(byName),
                    FingerprintOf(index.TryGet(byName.ServerName), byName.ToolName) ?? byName.Fingerprint)
                : null;
        }

        var summary = index.TryGet(serverName);
        var wrappers = catalog.WrappersFor(serverName);
        var wrapper = wrappers.FirstOrDefault(w => string.Equals(w.ToolName, tool, StringComparison.Ordinal))
                      ?? wrappers.FirstOrDefault(w => string.Equals(w.ToolName, tool, StringComparison.OrdinalIgnoreCase));
        if (wrapper is not null)
        {
            return new McpToolEntry(wrapper.ServerName, wrapper.ToolName, McpWrapperCatalog.RegistrationFor(wrapper),
                FingerprintOf(summary, wrapper.ToolName) ?? wrapper.Fingerprint);
        }

        // No wrapper: still a real tool if the index lists it, reachable through mcp_invoke_tool.
        if (summary is null)
            return null;

        var toolName = summary.ToolNames.FirstOrDefault(n => string.Equals(n, tool, StringComparison.Ordinal))
                       ?? summary.ToolNames.FirstOrDefault(n => string.Equals(n, tool, StringComparison.OrdinalIgnoreCase));
        return toolName is null
            ? null
            : new McpToolEntry(summary.ServerName, toolName, null, FingerprintOf(summary, toolName));
    }

    public IReadOnlyList<McpToolEntry> ForServer(string serverName)
    {
        var summary = index.TryGet(serverName);
        return catalog.WrappersFor(serverName)
            .OrderBy(w => w.ToolName, StringComparer.Ordinal)
            .Select(w => new McpToolEntry(w.ServerName, w.ToolName, McpWrapperCatalog.RegistrationFor(w),
                FingerprintOf(summary, w.ToolName) ?? w.Fingerprint))
            .ToList();
    }

    public bool IsServerIndexed(string serverName) =>
        !string.IsNullOrWhiteSpace(serverName) && index.TryGet(serverName) is not null;

    private static string? FingerprintOf(McpServerSummary? summary, string toolName) =>
        summary is not null && summary.ToolFingerprints.TryGetValue(toolName, out var fp) ? fp : null;
}
