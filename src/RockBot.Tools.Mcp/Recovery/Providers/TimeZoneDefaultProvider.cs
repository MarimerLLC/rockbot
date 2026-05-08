using RockBot.Host;

namespace RockBot.Tools.Mcp.Recovery.Providers;

/// <summary>
/// Resolves <c>timeZone</c>, <c>tz</c>, or <c>timezone</c> required-field errors
/// from <see cref="AgentClock.Zone"/> — the same source the system prompt builder reads.
/// </summary>
public sealed class TimeZoneDefaultProvider(AgentClock clock) : IToolArgumentDefaultsProvider
{
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        "timeZone", "timezone", "tz"
    };

    public bool CanResolve(string serverName, string toolName, string fieldName) =>
        Fields.Contains(fieldName);

    public Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct) =>
        Task.FromResult<ResolvedDefault?>(new ResolvedDefault(clock.Zone.Id));
}
