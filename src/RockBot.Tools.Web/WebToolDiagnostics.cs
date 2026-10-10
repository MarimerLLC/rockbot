using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Tools.Web;

/// <summary>
/// Metrics for the web tools, on the shared <c>RockBot.Tools</c> meter.
/// </summary>
internal static class WebToolDiagnostics
{
    public const string RateLimitedInstrumentName = "rockbot.tool.web_search.rate_limited";

    private static readonly Counter<long> RateLimited =
        ToolDiagnostics.Meter.CreateCounter<long>(
            RateLimitedInstrumentName,
            unit: "{response}",
            description: "Search provider responses that signalled a rate limit or overload (HTTP 429/503). " +
                         "Tags: provider, status, outcome (retried|exhausted). A steady rate means the " +
                         "provider tier is too low for the load.");

    /// <summary>Records one rate-limit response.</summary>
    /// <param name="exhausted">True when no retries were left and the search failed.</param>
    public static void RecordRateLimited(string provider, int statusCode, bool exhausted) =>
        RateLimited.Add(1, new TagList
        {
            { "provider", provider },
            { "status", statusCode },
            { "outcome", exhausted ? "exhausted" : "retried" }
        });
}
