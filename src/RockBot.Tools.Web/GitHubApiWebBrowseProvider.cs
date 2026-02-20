using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Web;

/// <summary>
/// Routes GitHub issue and pull-request URLs to the GitHub REST API so content
/// is returned without requiring JavaScript or authentication on public repos.
/// All other URLs fall through to the inner <see cref="IWebBrowseProvider"/>.
/// </summary>
internal sealed partial class GitHubApiWebBrowseProvider(
    HttpWebBrowseProvider fallback,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubApiWebBrowseProvider> logger) : IWebBrowseProvider
{
    // github.com/{owner}/{repo}/issues/{number}
    [GeneratedRegex(@"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/issues/(?<number>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IssueUrlPattern();

    // github.com/{owner}/{repo}/pull/{number}
    [GeneratedRegex(@"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/pull/(?<number>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PullUrlPattern();

    public async Task<WebPageContent> FetchAsync(string url, CancellationToken ct)
    {
        var issueMatch = IssueUrlPattern().Match(url);
        if (issueMatch.Success)
        {
            return await FetchIssueOrPrAsync(
                issueMatch.Groups["owner"].Value,
                issueMatch.Groups["repo"].Value,
                int.Parse(issueMatch.Groups["number"].Value),
                isPr: false,
                url,
                ct);
        }

        var prMatch = PullUrlPattern().Match(url);
        if (prMatch.Success)
        {
            return await FetchIssueOrPrAsync(
                prMatch.Groups["owner"].Value,
                prMatch.Groups["repo"].Value,
                int.Parse(prMatch.Groups["number"].Value),
                isPr: true,
                url,
                ct);
        }

        return await fallback.FetchAsync(url, ct);
    }

    private async Task<WebPageContent> FetchIssueOrPrAsync(
        string owner, string repo, int number, bool isPr, string originalUrl, CancellationToken ct)
    {
        var kind = isPr ? "pulls" : "issues";
        var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/{kind}/{number}";

        logger.LogDebug("Routing GitHub URL to API: {ApiUrl}", apiUrl);

        using var client = httpClientFactory.CreateClient("RockBot.Tools.Web.GitHub");
        JsonElement item;
        try
        {
            item = await client.GetFromJsonAsync<JsonElement>(apiUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("GitHub API request failed for {Url}: {Message}", apiUrl, ex.Message);
            return await fallback.FetchAsync(originalUrl, ct);
        }

        var title = item.TryGetString("title") ?? "(no title)";
        var state = item.TryGetString("state") ?? "unknown";
        var body = item.TryGetString("body") ?? "(no description)";
        var author = item.TryGetProperty("user")?.TryGetString("login") ?? "unknown";
        var createdAt = item.TryGetString("created_at") ?? string.Empty;
        var updatedAt = item.TryGetString("updated_at") ?? string.Empty;
        var htmlUrl = item.TryGetString("html_url") ?? originalUrl;

        var labels = item.TryGetProperty("labels") is JsonElement labelsEl
            ? labelsEl.EnumerateArray()
                .Select(l => l.TryGetString("name"))
                .Where(n => n != null)
                .ToList()
            : [];

        var sb = new StringBuilder();
        sb.AppendLine($"**Repository:** {owner}/{repo}");
        sb.AppendLine($"**State:** {state}");
        sb.AppendLine($"**Author:** {author}");
        if (!string.IsNullOrEmpty(createdAt)) sb.AppendLine($"**Created:** {createdAt}");
        if (!string.IsNullOrEmpty(updatedAt)) sb.AppendLine($"**Updated:** {updatedAt}");
        if (labels.Count > 0) sb.AppendLine($"**Labels:** {string.Join(", ", labels)}");
        sb.AppendLine($"**URL:** {htmlUrl}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(body);

        var kindLabel = isPr ? "PR" : "Issue";
        return new WebPageContent
        {
            Title = $"{kindLabel} #{number}: {title} [{owner}/{repo}]",
            Content = sb.ToString(),
            SourceUrl = originalUrl
        };
    }
}

/// <summary>
/// JsonElement extension helpers used only within this provider.
/// </summary>
file static class JsonElementExtensions
{
    internal static string? TryGetString(this JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    internal static JsonElement? TryGetProperty(this JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) ? prop : null;
    }
}
