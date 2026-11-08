using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace McpServer.Gmail.Services;

/// <summary>
/// Gmail REST transport. Every path is under <c>users/me</c> for the named account's token, so the
/// account comes from configuration, never from a tool argument.
/// </summary>
public sealed class GmailApi(HttpClient http, GoogleTokens tokens, IOptions<GmailOptions> options)
{
    public IReadOnlyList<GmailAccountOptions> Accounts => options.Value.Accounts;

    public GmailAccountOptions Account(string name) =>
        options.Value.Accounts.FirstOrDefault(a => a.Name == name)
        ?? throw new McpException(
            $"unknown account '{name}'; configured accounts: {string.Join(", ", options.Value.Accounts.Select(a => a.Name))}");

    public async Task<JsonDocument> GetAsync(string account, string relativeUri, CancellationToken ct) =>
        await SendAsync(account, HttpMethod.Get, relativeUri, null, ct) ?? throw new McpException("Gmail returned an empty response");

    public async Task<JsonDocument?> SendAsync(string account, HttpMethod method, string relativeUri, object? body, CancellationToken ct)
    {
        var a = Account(account);
        var json = body is null ? null : JsonSerializer.Serialize(body);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, $"users/me/{relativeUri}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(a, forceRefresh: attempt > 0, ct));
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                continue;   // The cached token may have been revoked early; refresh once.
            if (!response.IsSuccessStatusCode)
                throw new McpException(Describe(account, response.StatusCode, content));
            return string.IsNullOrWhiteSpace(content) ? null : JsonDocument.Parse(content);
        }
    }

    private static string Describe(string account, HttpStatusCode status, string content)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                detail = e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
        }

        var hint = status switch
        {
            HttpStatusCode.Forbidden => " The account didn't grant the needed permission, or its Workspace admin blocks the app; see deploy/gmail/README.md.",
            HttpStatusCode.NotFound => " The message, thread, draft or attachment was not found.",
            HttpStatusCode.TooManyRequests => " Gmail is rate-limiting this account; try again shortly.",
            _ => ""
        };
        return $"Gmail returned {(int)status} for account '{account}'{(detail.Length == 0 ? "" : $" ({detail})")}.{hint}";
    }
}
