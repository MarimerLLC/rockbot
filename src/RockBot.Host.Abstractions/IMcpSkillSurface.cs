namespace RockBot.Host;

/// <summary>
/// Freshness of an <c>mcp/{server}</c> skill relative to the server's live tool surface.
/// </summary>
public enum SkillFreshnessStatus
{
    /// <summary>
    /// Can't tell: the skill has no baseline (written before baselines existed, or while the
    /// server was unreadable), or the server's current surface can't be read.
    /// </summary>
    Unknown,

    /// <summary>The server's surface and version match what the skill was written against.</summary>
    Fresh,

    /// <summary>The server's surface or version changed since the skill was written.</summary>
    Stale,

    /// <summary>
    /// The server the skill was written for is no longer live under that name, but another live
    /// server carries the same id or canonical identity.
    /// </summary>
    Renamed
}

/// <summary>Result of evaluating an <c>mcp/{server}</c> skill's freshness.</summary>
/// <param name="Status">Freshness verdict.</param>
/// <param name="Reason">Short, LLM-facing explanation; null for <see cref="SkillFreshnessStatus.Fresh"/>.</param>
/// <param name="RenamedTo">For <see cref="SkillFreshnessStatus.Renamed"/>: the live server's current name.</param>
public sealed record SkillFreshness(SkillFreshnessStatus Status, string? Reason = null, string? RenamedTo = null)
{
    public static readonly SkillFreshness Fresh = new(SkillFreshnessStatus.Fresh);
}

/// <summary>
/// Result of trying to record a baseline for an <c>mcp/{server}</c> skill. Exactly one of
/// <see cref="Baseline"/> and <see cref="Reason"/> is set.
/// </summary>
public sealed record SkillBaselineCapture(SkillSurfaceBaseline? Baseline, string? Reason)
{
    public static SkillBaselineCapture Recorded(SkillSurfaceBaseline baseline) => new(baseline, null);
    public static SkillBaselineCapture NotRecorded(string reason) => new(null, reason);
}

/// <summary>
/// Reads the live MCP surface on behalf of skill code that can't reference the MCP gateway
/// (skill tools, repair tickets, the dream service). Implemented by <c>RockBot.Tools.Mcp</c>;
/// absent when the agent has no MCP gateway, in which case <c>mcp/</c> skills simply get no
/// baseline.
/// </summary>
public interface IMcpSkillSurface
{
    /// <summary>
    /// Records the current surface of the server an <c>mcp/{server}</c> or
    /// <c>mcp/{server}/*</c> skill documents. Returns no baseline, with a reason, when the
    /// server isn't connected or its surface couldn't be read in full — a failed read is never
    /// stored as a baseline.
    /// </summary>
    SkillBaselineCapture CaptureBaseline(string skillName);

    /// <summary>Compares a skill's recorded baseline with the server's current surface.</summary>
    SkillFreshness Evaluate(Skill skill);

    /// <summary>
    /// Renders the server's current tools (name, description, input schema) as text for an LLM
    /// to reconcile a skill against. Returns <c>null</c> when the schemas can't be fetched.
    /// </summary>
    Task<string?> GetLiveSurfaceTextAsync(string serverName, CancellationToken ct);
}

/// <summary>Naming rules for skills that document an MCP server.</summary>
public static class McpSkillNames
{
    /// <summary>
    /// Extracts <c>{server}</c> from <c>mcp/{server}</c> or <c>mcp/{server}/…</c>. The bare
    /// <c>mcp</c> guide and anything outside the <c>mcp/</c> prefix don't match.
    /// </summary>
    public static bool TryGetServerName(string? skillName, out string serverName)
    {
        serverName = string.Empty;
        if (string.IsNullOrWhiteSpace(skillName)
            || !skillName.StartsWith("mcp/", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = skillName.AsSpan(4);
        var slash = rest.IndexOf('/');
        var server = (slash < 0 ? rest : rest[..slash]).Trim();
        if (server.IsEmpty)
            return false;

        serverName = server.ToString().ToLowerInvariant();
        return true;
    }
}
