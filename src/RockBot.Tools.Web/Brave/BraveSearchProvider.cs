using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web.Brave;

internal sealed class BraveSearchProvider(
    IHttpClientFactory httpClientFactory,
    WebToolOptions options,
    ILogger<BraveSearchProvider> logger) : IWebSearchProvider
{
    private const string BaseUrl = "https://api.search.brave.com/res/v1/web/search";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var apiKey = options.ApiKey ?? Environment.GetEnvironmentVariable(options.ApiKeyEnvVar);
        if (string.IsNullOrEmpty(apiKey))
        {
            logger.LogWarning("Brave API key not set. Configure WebTools:ApiKey in user secrets or set the {EnvVar} environment variable", options.ApiKeyEnvVar);
            return [];
        }

        using var client = httpClientFactory.CreateClient("RockBot.Tools.Web.Brave");

        var count = Math.Min(maxResults, 20);
        var url = $"{BaseUrl}?q={Uri.EscapeDataString(query)}&count={count}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Subscription-Token", apiKey);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var braveResponse = JsonSerializer.Deserialize<BraveSearchResponse>(json, JsonOptions);

        if (braveResponse?.Web?.Results is null)
            return [];

        return braveResponse.Web.Results
            .Where(r => r.Title is not null && r.Url is not null)
            .Select(r => new WebSearchResult
            {
                Title = r.Title!,
                Url = r.Url!,
                Snippet = r.Description ?? string.Empty
            })
            .ToList();
    }
}
