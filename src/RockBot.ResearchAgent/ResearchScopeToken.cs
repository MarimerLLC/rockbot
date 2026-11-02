using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace RockBot.ResearchAgent;

/// <summary>
/// The <c>requestState</c> a synchronous (MRTR) <c>research</c> call carries between asking its
/// clarifying question and the retried call that brings the answer.
/// </summary>
/// <remarks>
/// <para>
/// The token comes back from the client, so it is untrusted: it is encrypted and authenticated with
/// ASP.NET Core Data Protection and expires after <see cref="ResearchOptions.ScopeTokenLifetime"/>.
/// It is also bound to the call's arguments, so a token cannot be replayed onto another question.
/// </para>
/// <para>
/// A token that fails any check reads as absent, and the tool simply scopes the question again.
/// The key ring is in-memory: a restart of the (single-replica) server invalidates outstanding
/// tokens, with the same outcome.
/// </para>
/// </remarks>
internal sealed class ResearchScopeToken
{
    private const string Purpose = "RockBot.ResearchAgent.scope.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _lifetime;

    public ResearchScopeToken(IDataProtectionProvider provider, TimeSpan lifetime)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _lifetime = lifetime;
    }

    /// <summary>Seals <paramref name="scope"/> for the call described by <paramref name="request"/>.</summary>
    public string Protect(ResearchRequest request, ResearchScope scope)
    {
        var payload = new Payload
        {
            Args = ArgumentHash(request),
            Interpretations = [.. scope.Interpretations.Select(i => new PayloadItem { Id = i.Id, Label = i.Label })],
            Best = scope.BestId,
        };
        return _protector.Protect(JsonSerializer.Serialize(payload), _lifetime);
    }

    /// <summary>
    /// The scope sealed in <paramref name="token"/>, or null when the token is missing, tampered
    /// with, expired, malformed, or was issued for different arguments.
    /// </summary>
    public ResearchScope? Unprotect(string? token, ResearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        try
        {
            var json = _protector.Unprotect(token, out _);
            var payload = JsonSerializer.Deserialize<Payload>(json);
            if (payload is null || payload.Args != ArgumentHash(request) || payload.Interpretations is null)
                return null;
            var scope = new ResearchScope(
                [.. payload.Interpretations
                    .Where(i => !string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Label))
                    .Select(i => new ResearchInterpretation(i.Id!, i.Label!))],
                payload.Best);
            return scope.IsAmbiguous ? scope : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 over the call's arguments, with lengths so fields can't run into each other.</summary>
    internal static string ArgumentHash(ResearchRequest request)
    {
        var sb = new StringBuilder();
        foreach (var part in new[] { request.Question, request.Context ?? "", request.Constraints ?? "" })
            sb.Append(part.Length).Append(':').Append(part).Append('|');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private sealed class Payload
    {
        public string? Args { get; set; }
        public List<PayloadItem>? Interpretations { get; set; }
        public string? Best { get; set; }
    }

    private sealed class PayloadItem
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
    }
}
