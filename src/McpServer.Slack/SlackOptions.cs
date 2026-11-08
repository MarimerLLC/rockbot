using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace McpServer.Slack;

public sealed partial class SlackOptions
{
    public const string SectionName = "Slack";

    /// <summary>The workspaces this server reads, each with its own user token.</summary>
    public List<SlackWorkspaceOptions> Workspaces { get; set; } = [];

    /// <summary>IANA or Windows time zone for the local times the tools return and accept.</summary>
    [Required] public string TimeZone { get; set; } = "UTC";

    /// <summary>Longest message text returned; longer messages are truncated.</summary>
    [Range(500, 100_000)] public int MaxMessageChars { get; set; } = 4_000;

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();
}

public sealed class SlackWorkspaceOptions
{
    /// <summary>Short name the tools use for this workspace, e.g. "work". Lower-case letters, digits and '-'.</summary>
    [Required] public string Name { get; set; } = string.Empty;

    /// <summary>A user token (xoxp-…) from this workspace's RockBot app.</summary>
    [Required] public string Token { get; set; } = string.Empty;

    /// <summary>Expected team id. When set, the server refuses a token that belongs to another workspace.</summary>
    public string TeamId { get; set; } = string.Empty;
}
