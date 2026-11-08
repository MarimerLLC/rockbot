using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace McpServer.Graph;

/// <summary>
/// One OneDrive or SharePoint document library the Drive tools can reach. Exactly one of
/// <see cref="SiteId"/> (a SharePoint site's document library) or <see cref="UserPrincipalName"/>
/// (a user's personal OneDrive) must be set. <see cref="Alias"/> is the leading path segment tool
/// arguments use to name this drive, e.g. "/finance-ro/Budgets/2026.xlsx" — never a Graph id, so the
/// MCP bridge's path-prefix argument guard can allow or deny it per alias.
/// </summary>
public sealed partial class GraphDriveEntry
{
    [Required] public string Alias { get; set; } = string.Empty;
    public string? SiteId { get; set; }
    public string? UserPrincipalName { get; set; }

    public static bool IsValidAlias(string alias) => AliasPattern().IsMatch(alias);

    // Lowercase, digits and hyphens only, so it round-trips cleanly as a URL path segment and can't
    // be confused with ".." or an empty segment when the arg guard normalizes the tool's path argument.
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex AliasPattern();
}

public static class GraphDriveOptionsValidation
{
    /// <summary>Checked once at startup via <c>ValidateOnStart</c>; returns null when every entry is well formed.</summary>
    public static string? Validate(IReadOnlyList<GraphDriveEntry> drives)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in drives)
        {
            if (!GraphDriveEntry.IsValidAlias(drive.Alias))
                return $"Graph:Drives alias '{drive.Alias}' must be lowercase letters, digits and hyphens only.";
            if (!seen.Add(drive.Alias))
                return $"Graph:Drives alias '{drive.Alias}' is configured more than once.";

            var hasSite = !string.IsNullOrWhiteSpace(drive.SiteId);
            var hasUser = !string.IsNullOrWhiteSpace(drive.UserPrincipalName);
            if (hasSite == hasUser)
                return $"Graph:Drives alias '{drive.Alias}' must set exactly one of SiteId or UserPrincipalName.";
        }
        return null;
    }
}
