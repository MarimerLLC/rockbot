using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web.Brave;

internal sealed class BraveSearchProvider : IWebSearchProvider
{
    private const string BaseUrl = "https://api.search.brave.com/res/v1/web/search";
    private const string ProviderName = "brave";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebToolOptions _options;
    private readonly ILogger<BraveSearchProvider> _logger;
    private readonly TimeProvider _time;

    // One throttle per provider instance. The provider is a DI singleton, so this is shared by
    // every session and subagent in the process: the quota is the API key's, not a session's.
    private readonly WebSearchThrottle _throttle;

    public BraveSearchProvider(
        IHttpClientFactory httpClientFactory,
        WebToolOptions options,
        ILogger<BraveSearchProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _throttle = new WebSearchThrottle(
            TimeSpan.FromMilliseconds(Math.Max(0, options.SearchMinIntervalMs)), _time);
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var apiKey = _options.ApiKey ?? Environment.GetEnvironmentVariable(_options.ApiKeyEnvVar);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Brave API key not set. Configure WebTools:ApiKey in user secrets or set the {EnvVar} environment variable", _options.ApiKeyEnvVar);
            // Signal a configuration gap rather than returning an empty result set, which the
            // caller can't distinguish from a search that genuinely found nothing.
            throw new WebSearchNotConfiguredException(
                $"Web search is unavailable because the Brave Search API key is not configured. " +
                $"Set WebTools:ApiKey (config / user secrets) or the {_options.ApiKeyEnvVar} environment variable.");
        }

        using var client = _httpClientFactory.CreateClient("RockBot.Tools.Web.Brave");

        var count = Math.Min(maxResults, 20);
        var url = $"{BaseUrl}?q={Uri.EscapeDataString(query)}&count={count}";
        var maxRetries = Math.Max(0, _options.SearchMaxRetries);

        // Hold the provider for the whole search, retries included: parallel searches queue
        // here instead of all reaching the provider in the same second.
        using var lease = await _throttle.AcquireAsync(ct);

        for (var attempt = 0; ; attempt++)
        {
            await lease.WaitForSlotAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("X-Subscription-Token", apiKey);

            using var response = await client.SendAsync(request, ct);

            if (SearchRetryPolicy.IsRateLimited(response.StatusCode))
            {
                var status = (int)response.StatusCode;
                var delay = SearchRetryPolicy.ComputeDelay(response, attempt + 1, _options, _time.GetUtcNow());

                // Whether or not this search retries, the provider asked for a pause, so hold
                // back the next request start: this search's retry, or the next caller's search.
                lease.DeferNextSlot(delay);

                if (attempt >= maxRetries)
                {
                    WebToolDiagnostics.RecordRateLimited(ProviderName, status, exhausted: true);
                    _logger.LogWarning(
                        "Brave search still rate-limited (HTTP {Status}) after {Retries} retries; giving up",
                        status, attempt);
                    throw new WebSearchRateLimitedException(status, attempt);
                }

                WebToolDiagnostics.RecordRateLimited(ProviderName, status, exhausted: false);
                _logger.LogInformation(
                    "Brave search rate-limited (HTTP {Status}); retry {Retry} of {MaxRetries} in {DelayMs} ms",
                    status, attempt + 1, maxRetries, (long)delay.TotalMilliseconds);
                continue;
            }

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
}
