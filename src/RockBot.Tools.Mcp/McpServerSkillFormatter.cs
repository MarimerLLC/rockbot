using System.Text;
using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Looks up <c>mcp/{server}</c> (and any <c>mcp/{server}/*</c> sub-skills) from
/// <see cref="ISkillStore"/> and renders them into a compact markdown block.
/// Both the <c>mcp_get_service_details</c> pre-flight path and the recovery-time
/// <see cref="Recovery.SchemaErrorEnricher"/> append the same block to their output
/// so the LLM sees verified parameter shape and usage notes in the same turn it
/// receives the tool response.
/// </summary>
internal static class McpServerSkillFormatter
{
    /// <summary>
    /// Per-skill content cap when rendering. Skill bodies above this are
    /// truncated with a tail marker — the goal is to keep the injected block
    /// from dominating the LLM context when a single skill body is unusually
    /// large.
    /// </summary>
    internal const int PerSkillContentCap = 4_000;

    /// <summary>
    /// Returns the formatted skill block for <paramref name="serverName"/>, or
    /// <c>null</c> when no matching skills exist or the store is unavailable.
    /// Lookup is case-insensitive on <c>mcp/{server}</c>.
    /// </summary>
    public static async Task<string?> FormatAsync(
        ISkillStore? skillStore,
        string serverName,
        CancellationToken ct)
    {
        if (skillStore is null || string.IsNullOrWhiteSpace(serverName))
            return null;

        // ListAsync is the only prefix-aware API on ISkillStore. Skill counts on
        // production agents are in the low hundreds, so a single list+filter is
        // cheaper than two separate Get calls plus a second filtered scan, and
        // avoids the case where a sub-skill exists without the canonical parent.
        IReadOnlyList<Skill> all;
        try
        {
            all = await skillStore.ListAsync();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }

        var canonicalName = $"mcp/{serverName.ToLowerInvariant()}";
        var subPrefix = canonicalName + "/";

        var matches = all
            .Where(s => string.Equals(s.Name, canonicalName, StringComparison.OrdinalIgnoreCase)
                        || s.Name.StartsWith(subPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.Append("[mcp-skill-injection] Skills already document `").Append(serverName)
          .AppendLine("`. Read them before authoring the next call — these capture verified parameter shapes, required fields, and quirks from prior sessions.");
        foreach (var skill in matches)
        {
            sb.AppendLine();
            sb.Append("### Skill: `").Append(skill.Name).AppendLine("`");
            if (!string.IsNullOrWhiteSpace(skill.Summary))
                sb.Append("_").Append(skill.Summary.Trim()).AppendLine("_");
            sb.AppendLine();

            var body = skill.Content ?? string.Empty;
            if (body.Length > PerSkillContentCap)
            {
                sb.Append(body.AsSpan(0, PerSkillContentCap));
                sb.Append("\n…[truncated — full skill body is ").Append(body.Length)
                  .Append(" chars; load with get_skill(\"").Append(skill.Name).AppendLine("\") for the rest]");
            }
            else
            {
                sb.AppendLine(body);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
