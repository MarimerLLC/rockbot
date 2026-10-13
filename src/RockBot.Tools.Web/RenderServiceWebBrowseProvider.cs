using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web;

/// <summary>
/// Renders a page in the headless browser of the <c>RockBot.Browser</c> render service and
/// converts the resulting DOM to Markdown. The service runs JavaScript and clicks away common
/// cookie-consent banners; it has no credentials, so login-gated pages stay out of reach.
/// </summary>
internal sealed class RenderServiceWebBrowseProvider(
    IHttpClientFactory httpClientFactory,
    WebToolOptions options,
    ILogger<RenderServiceWebBrowseProvider> logger) : IWebBrowseProvider
{
    internal const string HttpClientName = "RockBot.Tools.Web.Render";

    /// <summary>True when a render service is configured.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(options.RenderServiceUrl);

    public async Task<WebPageContent> FetchAsync(string url, CancellationToken ct)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("No render service is configured.");

        using var client = httpClientFactory.CreateClient(HttpClientName);
        var endpoint = $"{options.RenderServiceUrl!.TrimEnd('/')}/render";
        var request = new RenderRequest(url, options.RenderTimeoutSeconds * 1000);

        using var response = await client.PostAsJsonAsync(endpoint, request, ct);
        var result = await response.Content.ReadFromJsonAsync<RenderResponse>(ct)
            ?? throw new InvalidOperationException("The render service returned an empty response.");

        if (!string.IsNullOrWhiteSpace(result.Error))
            throw new InvalidOperationException(result.Error);
        response.EnsureSuccessStatusCode();
        if (result.HttpStatus >= 400)
            throw new HttpRequestException(
                $"The rendered page answered HTTP {result.HttpStatus}.", null, (HttpStatusCode)result.HttpStatus);

        var page = await HtmlPageConverter.ConvertAsync(result.Html ?? string.Empty, options.ShellMinContentChars, ct);

        logger.LogInformation(
            "Rendered {Url} in the headless browser ({Length} chars of Markdown, status {Status}, consent dismissed {Dismissed})",
            url, page.Markdown.Length, page.Status, result.ConsentDismissed);

        return new WebPageContent
        {
            Title = string.IsNullOrWhiteSpace(page.Title) ? result.Title ?? string.Empty : page.Title,
            Content = page.Markdown,
            SourceUrl = string.IsNullOrWhiteSpace(result.FinalUrl) ? url : result.FinalUrl,
            Status = page.Status,
            FetchedVia = WebFetchMethod.Browser,
            ConsentDismissed = result.ConsentDismissed
        };
    }

    internal sealed record RenderRequest(string Url, int TimeoutMs);

    internal sealed record RenderResponse(
        string? FinalUrl,
        int HttpStatus,
        string? Title,
        string? Html,
        bool ConsentDismissed,
        string? Error);
}
