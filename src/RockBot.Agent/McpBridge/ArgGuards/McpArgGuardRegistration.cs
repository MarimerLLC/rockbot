namespace RockBot.Agent.McpBridge.ArgGuards;

/// <summary>
/// Binds a handler name to a guard instance for DI registration. Register one per
/// handler alongside <see cref="McpArgGuardRegistry"/>, mirroring the token-provider
/// registry pattern:
/// <code>
/// services.AddSingleton(new McpArgGuardRegistration(PathPrefixArgGuard.HandlerName, new PathPrefixArgGuard()));
/// services.AddSingleton&lt;IMcpArgGuardRegistry, McpArgGuardRegistry&gt;();
/// </code>
/// </summary>
public sealed record McpArgGuardRegistration(string Handler, IMcpArgGuard Guard);

/// <summary>
/// Resolves <see cref="IMcpArgGuard"/> handlers by the name used in mcp.json
/// <c>argGuards</c> entries.
/// </summary>
public interface IMcpArgGuardRegistry
{
    /// <summary>
    /// Returns the guard registered under <paramref name="handler"/>. Throws
    /// <see cref="KeyNotFoundException"/> listing the known handlers when the name is unknown.
    /// </summary>
    IMcpArgGuard Get(string handler);

    bool Contains(string handler);

    IReadOnlyCollection<string> KnownHandlers { get; }
}

public sealed class McpArgGuardRegistry : IMcpArgGuardRegistry
{
    private readonly Dictionary<string, IMcpArgGuard> _guards;

    public McpArgGuardRegistry(IEnumerable<McpArgGuardRegistration> registrations)
    {
        _guards = registrations.ToDictionary(
            r => r.Handler,
            r => r.Guard,
            StringComparer.OrdinalIgnoreCase);
    }

    public IMcpArgGuard Get(string handler)
    {
        if (_guards.TryGetValue(handler, out var guard))
            return guard;
        throw new KeyNotFoundException(
            $"No MCP argument guard is registered for handler '{handler}'. " +
            $"Known handlers: [{string.Join(", ", _guards.Keys)}].");
    }

    public bool Contains(string handler) => _guards.ContainsKey(handler);

    public IReadOnlyCollection<string> KnownHandlers => _guards.Keys;
}
