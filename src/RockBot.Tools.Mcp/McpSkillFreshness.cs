using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Pure freshness rules for <c>mcp/{server}</c> skills (#615, ports mcp-aggregator#47). Kept free
/// of I/O so every rule is testable against hand-built summaries.
/// <para>
/// The aggregator's version got three things wrong (aggregator#41); each has a rule here:
/// the fingerprint covers descriptions and schemas, not just names
/// (<see cref="McpSurfaceFingerprint"/>); a recorded version is actually compared; and a failed
/// read never becomes a baseline (<see cref="Capture"/>), so it can't pin a skill at stale.
/// </para>
/// </summary>
internal static class McpSkillFreshness
{
    /// <summary>
    /// Records <paramref name="current"/> as the surface a skill for <paramref name="serverName"/>
    /// was written against, or explains why nothing can be recorded.
    /// </summary>
    public static SkillBaselineCapture Capture(string serverName, McpServerSummary? current, DateTimeOffset now)
    {
        if (current is null)
            return SkillBaselineCapture.NotRecorded($"MCP server '{serverName}' isn't connected");
        if (current.Fingerprint is null)
            return SkillBaselineCapture.NotRecorded($"MCP server '{serverName}' surface couldn't be read in full");

        return SkillBaselineCapture.Recorded(new SkillSurfaceBaseline(
            ServerName: current.ServerName,
            ServerId: current.ServerId,
            Fingerprint: current.Fingerprint,
            Version: current.Version,
            IdentityHash: current.IdentityHash,
            RecordedAt: now));
    }

    /// <summary>
    /// Freshness of a skill documenting <paramref name="serverName"/>, given its recorded
    /// <paramref name="baseline"/>, the live summary under that name (<paramref name="current"/>)
    /// and every live summary (<paramref name="all"/>, for rename detection).
    /// </summary>
    public static SkillFreshness Evaluate(
        string serverName,
        SkillSurfaceBaseline? baseline,
        McpServerSummary? current,
        IReadOnlyList<McpServerSummary> all)
    {
        // Skills written before baselines existed, or while the server was unreadable.
        if (baseline is null)
            return new SkillFreshness(SkillFreshnessStatus.Unknown, "no surface baseline recorded");

        if (current is null)
        {
            var renamed = all.FirstOrDefault(s =>
                !string.Equals(s.ServerName, serverName, StringComparison.OrdinalIgnoreCase)
                && SameServer(baseline, s));
            if (renamed is not null)
            {
                return new SkillFreshness(
                    SkillFreshnessStatus.Renamed,
                    $"this skill was written for MCP server '{serverName}', which is now registered as '{renamed.ServerName}'",
                    renamed.ServerName);
            }

            // Possibly just not connected yet; nothing to compare against.
            return new SkillFreshness(SkillFreshnessStatus.Unknown, $"MCP server '{serverName}' isn't connected");
        }

        // Same name, different server entry: the name was reused for something else.
        if (baseline.ServerId is not null && current.ServerId is not null
            && !string.Equals(baseline.ServerId, current.ServerId, StringComparison.Ordinal)
            && !SameIdentity(baseline, current))
        {
            return new SkillFreshness(
                SkillFreshnessStatus.Stale,
                $"the name '{serverName}' now refers to a different MCP server than the one this skill was written for");
        }

        if (current.Fingerprint is null)
            return new SkillFreshness(SkillFreshnessStatus.Unknown, $"MCP server '{serverName}' surface couldn't be read in full");

        if (baseline.Version is not null && current.Version is not null
            && !string.Equals(baseline.Version, current.Version, StringComparison.Ordinal))
        {
            return new SkillFreshness(
                SkillFreshnessStatus.Stale,
                $"{serverName} changed from v{baseline.Version} to v{current.Version} since this skill was written");
        }

        return string.Equals(baseline.Fingerprint, current.Fingerprint, StringComparison.Ordinal)
            ? SkillFreshness.Fresh
            : new SkillFreshness(
                SkillFreshnessStatus.Stale,
                $"{serverName}'s tool surface changed since this skill was written");
    }

    private static bool SameServer(SkillSurfaceBaseline baseline, McpServerSummary summary) =>
        (baseline.ServerId is not null
         && string.Equals(baseline.ServerId, summary.ServerId, StringComparison.Ordinal))
        || SameIdentity(baseline, summary);

    private static bool SameIdentity(SkillSurfaceBaseline baseline, McpServerSummary summary) =>
        baseline.IdentityHash is not null
        && string.Equals(baseline.IdentityHash, summary.IdentityHash, StringComparison.Ordinal);
}
