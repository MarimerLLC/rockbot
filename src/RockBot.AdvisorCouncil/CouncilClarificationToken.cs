using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil;

/// <summary>
/// The <c>requestState</c> a plain (MRTR) <c>advise</c> call carries between asking its clarifying
/// question and the retried call that brings the answer: the Select step's output, so the retry
/// doesn't run Select again.
/// </summary>
/// <remarks>
/// The token comes back from the client, so it is untrusted: it is encrypted and authenticated
/// with ASP.NET Core Data Protection, expires after <see cref="Council.CouncilOptions.ClarificationTokenLifetime"/>,
/// and is bound to the call's arguments. A token that fails any check reads as absent, and the
/// tool runs Select again without asking. Same scheme as the research server's
/// <c>ResearchScopeToken</c>.
/// </remarks>
internal sealed class CouncilClarificationToken
{
    private const string Purpose = "RockBot.AdvisorCouncil.select.v1";

    private static readonly JsonSerializerOptions Json = new();

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _lifetime;

    public CouncilClarificationToken(IDataProtectionProvider provider, TimeSpan lifetime)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _lifetime = lifetime;
    }

    /// <summary>Seals <paramref name="selection"/> for the call described by <paramref name="request"/>.</summary>
    public string Protect(CouncilRequest request, SelectorOutput selection)
    {
        var payload = new Payload { Args = ArgumentHash(request), Selection = selection };
        return _protector.Protect(JsonSerializer.Serialize(payload, Json), _lifetime);
    }

    /// <summary>
    /// The selection sealed in <paramref name="token"/>, or null when the token is missing, tampered
    /// with, expired, malformed, or was issued for different arguments.
    /// </summary>
    public SelectorOutput? Unprotect(string? token, CouncilRequest request)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        try
        {
            var json = _protector.Unprotect(token, out _);
            var payload = JsonSerializer.Deserialize<Payload>(json, Json);
            if (payload?.Selection is not { Personas.Count: > 0 } selection || payload.Args != ArgumentHash(request))
                return null;
            return selection;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 over the call's arguments, with lengths so fields can't run into each other.</summary>
    internal static string ArgumentHash(CouncilRequest request)
    {
        var sb = new StringBuilder();
        IEnumerable<string> parts =
        [
            request.Question, request.Context ?? "",
            string.Join("\u001f", request.Options), string.Join("\u001f", request.Personas),
            request.TimeHorizon ?? "", request.RiskAppetite ?? "", request.Reversibility ?? "",
            request.Critique.ToString(), request.PreResearch.ToString(),
        ];
        foreach (var part in parts)
            sb.Append(part.Length).Append(':').Append(part).Append('|');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private sealed class Payload
    {
        public string? Args { get; set; }
        public SelectorOutput? Selection { get; set; }
    }
}
