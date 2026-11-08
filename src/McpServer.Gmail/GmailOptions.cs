using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace McpServer.Gmail;

public sealed partial class GmailOptions
{
    public const string SectionName = "Gmail";

    /// <summary>The Google Cloud OAuth client (type "Desktop app") every account consented to.</summary>
    [Required] public string ClientId { get; set; } = string.Empty;
    [Required] public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The Gmail accounts this server reads, each with its own refresh token.</summary>
    public List<GmailAccountOptions> Accounts { get; set; } = [];

    /// <summary>IANA or Windows time zone for the dates the tools return.</summary>
    [Required] public string TimeZone { get; set; } = "UTC";

    [Range(1_000, 200_000)] public int MaxBodyChars { get; set; } = 20_000;

    /// <summary>Largest attachment get_attachment downloads. Gmail messages are at most 25 MB.</summary>
    [Range(1_024, 52_428_800)] public long MaxAttachmentBytes { get; set; } = 25 * 1_048_576;

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();
}

public sealed class GmailAccountOptions
{
    /// <summary>Short name the tools use for this account, e.g. "personal" or "work".</summary>
    [Required] public string Name { get; set; } = string.Empty;

    /// <summary>The account's address; drafts are written from it and the server checks the token belongs to it.</summary>
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;

    [Required] public string RefreshToken { get; set; } = string.Empty;
}
