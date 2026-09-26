using System.Text.RegularExpressions;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Replaces secret-shaped text with <c>[redacted]</c> before free-form content — conversation
/// turns, tool-argument values — goes into an elicitation responder's prompt.
/// </summary>
/// <remarks>
/// <para>
/// Secrets are never meant to reach LLM context (<c>design/security.md</c>), but a user can paste
/// one into a message, and conversation memory stores the message as written. The main agent's
/// loop has already seen it; a responder may run on a different model and provider, so it should
/// not see it again.
/// </para>
/// <para>
/// This is a filter on the way into a prompt, not a detector anyone relies on for correctness:
/// it errs toward redacting (a long hash or opaque id may be caught), because a responder only
/// needs the meaning of the conversation, never an exact token. Patterns: well-known key formats,
/// bearer tokens and JWTs, PEM private keys, credential-named <c>key=value</c> / <c>key: value</c>
/// pairs, card-like and SSN-like digit runs, and long mixed letter-digit tokens.
/// </para>
/// </remarks>
public static partial class McpSecretScrubber
{
    /// <summary>What a redacted secret is replaced with.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>Returns <paramref name="text"/> with secret-shaped substrings redacted.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var scrubbed = PrivateKeyBlock().Replace(text, Redacted);
        scrubbed = CredentialAssignment().Replace(scrubbed, m => m.Groups["key"].Value + m.Groups["sep"].Value + Redacted);
        scrubbed = BearerToken().Replace(scrubbed, m => m.Groups["scheme"].Value + " " + Redacted);
        scrubbed = KnownKeyFormat().Replace(scrubbed, Redacted);
        scrubbed = Jwt().Replace(scrubbed, Redacted);
        scrubbed = CardNumber().Replace(scrubbed, Redacted);
        scrubbed = Ssn().Replace(scrubbed, Redacted);
        scrubbed = LongMixedToken().Replace(scrubbed, m => HasLetterAndDigit(m.Value) ? Redacted : m.Value);
        return scrubbed;
    }

    private static bool HasLetterAndDigit(string value)
        => value.Any(char.IsLetter) && value.Any(char.IsDigit);

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(-----END [A-Z ]*PRIVATE KEY-----|$)",
        RegexOptions.Singleline)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(
        @"(?<key>\b(?:password|passwd|pwd|passcode|passphrase|secret|client[_\- ]?secret|api[_\- ]?key|access[_\- ]?key|secret[_\- ]?key|private[_\- ]?key|token|access[_\- ]?token|refresh[_\- ]?token|auth[_\- ]?token|pin)\b)(?<sep>\s*(?:[:=]|\bis\b)\s*)[""']?[^\s""',;]+[""']?",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialAssignment();

    [GeneratedRegex(@"(?<scheme>\b(?:Bearer|Basic|Token))\s+[A-Za-z0-9._~+/\-]{12,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(
        @"\b(?:sk-(?:proj-|ant-|live-|test-)?[A-Za-z0-9_\-]{16,}|sk_(?:live|test)_[A-Za-z0-9]{16,}|rk_(?:live|test)_[A-Za-z0-9]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|glpat-[A-Za-z0-9_\-]{20,}|npm_[A-Za-z0-9]{30,})")]
    private static partial Regex KnownKeyFormat();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:\d[ \-]?){12,18}\d\b")]
    private static partial Regex CardNumber();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex Ssn();

    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{32,}={0,2}")]
    private static partial Regex LongMixedToken();
}
