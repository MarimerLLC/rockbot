using System.Text;
using RockBot.Host;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Mcp;

/// <summary>
/// <see cref="IMcpSkillSurface"/> over the agent's <see cref="McpServerIndex"/> (fingerprints,
/// version, identity) and <see cref="ToolSchemaCache"/> (schemas for the dream refresh pass).
/// </summary>
internal sealed class McpSkillSurface(
    McpServerIndex index,
    ToolSchemaCache schemas,
    TimeProvider? timeProvider = null) : IMcpSkillSurface
{
    /// <summary>Cap on the rendered surface handed to the dream refresh prompt.</summary>
    internal const int LiveSurfaceTextCap = 24_000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public SkillBaselineCapture CaptureBaseline(string skillName)
    {
        if (!McpSkillNames.TryGetServerName(skillName, out var server))
            return SkillBaselineCapture.NotRecorded($"'{skillName}' doesn't document an MCP server");

        return McpSkillFreshness.Capture(server, index.TryGet(server), _time.GetUtcNow());
    }

    public SkillFreshness Evaluate(Skill skill)
    {
        if (!McpSkillNames.TryGetServerName(skill.Name, out var server))
            return new SkillFreshness(SkillFreshnessStatus.Unknown, "not an MCP server skill");

        var all = index.Snapshot();
        var current = all.FirstOrDefault(s => string.Equals(s.ServerName, server, StringComparison.OrdinalIgnoreCase));
        return McpSkillFreshness.Evaluate(server, skill.SurfaceBaseline, current, all);
    }

    public async Task<string?> GetLiveSurfaceTextAsync(string serverName, CancellationToken ct)
    {
        var tools = await schemas.GetServerAsync(serverName, ct);
        if (tools is null)
            return null;

        var sb = new StringBuilder();
        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var entry = new StringBuilder()
                .Append("### ").AppendLine(tool.Name)
                .AppendLine(string.IsNullOrWhiteSpace(tool.Description) ? "(no description)" : tool.Description.Trim())
                .Append("Input schema: ")
                .AppendLine(string.IsNullOrWhiteSpace(tool.ParametersSchema) ? "(none)" : McpSurfaceFingerprint.Canonicalize(tool.ParametersSchema))
                .AppendLine()
                .ToString();

            if (sb.Length + entry.Length > LiveSurfaceTextCap)
            {
                sb.Append("…[truncated — ").Append(tools.Count).AppendLine(" tools in total]");
                break;
            }
            sb.Append(entry);
        }

        return sb.ToString().TrimEnd();
    }
}
