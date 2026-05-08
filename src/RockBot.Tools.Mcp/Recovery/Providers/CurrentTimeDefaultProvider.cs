using RockBot.Host;

namespace RockBot.Tools.Mcp.Recovery.Providers;

/// <summary>
/// Resolves <c>now</c>, <c>currentTime</c>, or <c>referenceTime</c> required-field
/// errors using <see cref="AgentClock.Now"/>. Returns ISO-8601 with offset.
/// </summary>
public sealed class CurrentTimeDefaultProvider(AgentClock clock) : IToolArgumentDefaultsProvider
{
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        "now", "currentTime", "referenceTime"
    };

    public bool CanResolve(string serverName, string toolName, string fieldName) =>
        Fields.Contains(fieldName);

    public Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct) =>
        Task.FromResult<ResolvedDefault?>(new ResolvedDefault(clock.Now.ToString("o")));
}
