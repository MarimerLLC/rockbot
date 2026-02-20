using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpServer.OpenRouter.Options;
using Microsoft.Extensions.Options;

namespace McpServer.OpenRouter.Services;

/// <summary>
/// Lightweight HTTP client for the OpenRouter REST API.
/// All methods return the raw JSON payload as a <see cref="JsonNode"/> so that
/// the MCP tools can serialise the full, up-to-date response without needing
/// per-field mapping.
/// </summary>
public sealed class OpenRouterClient
{
    private readonly HttpClient _http;

    public OpenRouterClient(HttpClient http, IOptions<OpenRouterOptions> options)
    {
        _http = http;
        var opts = options.Value;
        _http.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/');
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        _http.DefaultRequestHeaders.Add(
            "HTTP-Referer", "https://github.com/MarimerLLC/rockbot");
    }

    /// <summary>Gets the credit balance for the account associated with the API key.</summary>
    public Task<JsonNode?> GetCreditsAsync(CancellationToken ct = default)
        => GetJsonAsync("credits", ct);

    /// <summary>Gets details about the current API key (rate limits, usage, etc.).</summary>
    public Task<JsonNode?> GetApiKeyInfoAsync(CancellationToken ct = default)
        => GetJsonAsync("auth/key", ct);

    /// <summary>Lists all available models on OpenRouter.</summary>
    public Task<JsonNode?> ListModelsAsync(CancellationToken ct = default)
        => GetJsonAsync("models", ct);

    /// <summary>
    /// Lists provisioned API keys for the organisation.
    /// Requires a management (provisioned) key.
    /// </summary>
    public Task<JsonNode?> ListApiKeysAsync(CancellationToken ct = default)
        => GetJsonAsync("keys", ct);

    /// <summary>
    /// Gets details for a specific provisioned API key by its hash.
    /// Requires a management (provisioned) key.
    /// </summary>
    public Task<JsonNode?> GetApiKeyAsync(string keyHash, CancellationToken ct = default)
        => GetJsonAsync($"keys/{Uri.EscapeDataString(keyHash)}", ct);

    /// <summary>Gets details for a specific generation (completion) by ID.</summary>
    public Task<JsonNode?> GetGenerationAsync(string generationId, CancellationToken ct = default)
        => GetJsonAsync($"generation?id={Uri.EscapeDataString(generationId)}", ct);

    // ── internals ──────────────────────────────────────────────────────────────

    private async Task<JsonNode?> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(relativeUrl, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenRouter API error {(int)response.StatusCode} for '{relativeUrl}': {body}",
                null,
                response.StatusCode);
        }

        return JsonNode.Parse(body);
    }
}
