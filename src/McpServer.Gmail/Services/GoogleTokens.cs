using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace McpServer.Gmail.Services;

/// <summary>
/// Exchanges each account's refresh token for short-lived access tokens, cached until five
/// minutes before they expire.
/// </summary>
public sealed class GoogleTokens(HttpClient http, IOptions<GmailOptions> options, TimeProvider clock)
{
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<string> GetAsync(GmailAccountOptions account, bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _cache.TryGetValue(account.Name, out var hit) && clock.GetUtcNow() < hit.Expires - Margin)
            return hit.Token;

        var gate = _locks.GetOrAdd(account.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cache.TryGetValue(account.Name, out hit) && clock.GetUtcNow() < hit.Expires - Margin)
                return hit.Token;

            var o = options.Value;
            using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = account.RefreshToken,
                ["client_id"] = o.ClientId,
                ["client_secret"] = o.ClientSecret
            }), ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var token))
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : $"HTTP {(int)response.StatusCode}";
                throw new McpException(error == "invalid_grant"
                    ? $"Google no longer accepts the sign-in for account '{account.Name}' (invalid_grant): it was revoked, the password changed, " +
                      "or the OAuth consent screen is in Testing mode, where tokens expire after 7 days. Re-run deploy/gmail/setup.sh --account " + account.Name
                    : $"could not get a Google access token for account '{account.Name}': {error}");
            }

            var seconds = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var s) ? s : 3600;
            var value = token.GetString()!;
            _cache[account.Name] = (value, clock.GetUtcNow().AddSeconds(seconds));
            return value;
        }
        finally
        {
            gate.Release();
        }
    }
}
