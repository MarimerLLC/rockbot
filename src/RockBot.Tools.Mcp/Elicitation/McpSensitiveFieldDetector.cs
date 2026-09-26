using System.Text;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Recognizes elicitation fields that ask for a secret.
/// </summary>
/// <remarks>
/// <para>
/// The MCP specification says servers MUST NOT use elicitation to request credentials, but a
/// client that relies on servers behaving is not a security boundary. RockBot's model is that
/// nothing trusts the LLM, and a responder answering "what is the API key?" from context is
/// exactly the failure this guards against: secrets are never in LLM context by design, so any
/// value it produced for such a field would be a hallucination or a leak.
/// </para>
/// <para>
/// Matching runs over the field's name, title, and description. A false positive costs one
/// declined elicitation, which the agent is told about and can answer another way; a false
/// negative hands a secret-shaped field to a responder, so the bias is toward declining.
/// </para>
/// </remarks>
public static class McpSensitiveFieldDetector
{
    /// <summary>
    /// Phrases matched anywhere in the normalized text, in both spaced and run-together form,
    /// so "api key", "api_key" and "apiKey" are one needle.
    /// </summary>
    private static readonly string[] PhraseNeedles =
    [
        "api key", "private key", "ssh key", "secret key", "client secret",
        "connection string", "social security", "credit card", "card number",
        "security code", "account number", "routing number", "pin code",
        "one time", "auth code", "authorization code", "mfa code", "access code",
        "two factor", "2 fa",
    ];

    /// <summary>
    /// Single words matched as whole tokens. Substring matching is deliberately avoided here:
    /// "maxTokens" and "slotPosition" must not read as "token" and "otp".
    /// </summary>
    private static readonly string[] WordNeedles =
    [
        "password", "passwords", "passwd", "passphrase", "secret", "secrets",
        "token", "credential", "credentials", "apikey", "privatekey", "creditcard",
        "ssn", "cvv", "cvc", "otp", "mfa", "pin", "bearer", "auth",
    ];

    /// <summary>
    /// Returns the field names in <paramref name="schema"/> that look like they are asking for
    /// a secret, or that appear in <paramref name="deniedFields"/>.
    /// </summary>
    public static IReadOnlyList<string> FindSensitiveFields(
        ElicitRequestParams.RequestSchema? schema,
        IReadOnlyCollection<string>? deniedFields = null)
    {
        if (schema?.Properties is not { Count: > 0 } properties)
            return [];

        var denied = deniedFields is { Count: > 0 }
            ? new HashSet<string>(deniedFields, StringComparer.OrdinalIgnoreCase)
            : null;

        List<string>? hits = null;
        foreach (var pair in properties)
        {
            if (denied?.Contains(pair.Key) == true || IsSensitive(pair.Key, pair.Value))
            {
                hits ??= [];
                hits.Add(pair.Key);
            }
        }

        return hits ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// Whether a single field looks like it is asking for a secret, judged from its name and
    /// any human-readable title or description the server attached to it.
    /// </summary>
    public static bool IsSensitive(string fieldName, ElicitRequestParams.PrimitiveSchemaDefinition? definition = null)
    {
        if (Matches(fieldName)) return true;
        if (definition is null) return false;
        return Matches(definition.Title) || Matches(definition.Description);
    }

    private static bool Matches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var spaced = Normalize(text);
        var collapsed = spaced.Replace(" ", string.Empty);

        foreach (var phrase in PhraseNeedles)
        {
            if (spaced.Contains(phrase, StringComparison.Ordinal))
                return true;
            if (collapsed.Contains(phrase.Replace(" ", string.Empty), StringComparison.Ordinal))
                return true;
        }

        var tokens = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            foreach (var word in WordNeedles)
            {
                if (string.Equals(token, word, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Lower-cases the text and turns underscores, hyphens and camelCase humps into spaces so
    /// every common field-naming convention normalizes to the same words.
    /// </summary>
    private static string Normalize(string text)
    {
        var buffer = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                buffer.Append(' ');
                continue;
            }

            if (char.IsUpper(c) && i > 0 && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1])))
                buffer.Append(' ');

            buffer.Append(char.ToLowerInvariant(c));
        }

        return buffer.ToString();
    }
}
