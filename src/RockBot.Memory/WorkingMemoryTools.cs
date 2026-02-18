using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Memory;

/// <summary>
/// LLM-callable tools for session-scoped working memory — a scratch space for caching
/// large or expensive-to-fetch tool results so they can be referenced in follow-up turns
/// without re-calling the external source.
///
/// Instantiated per-message with the session ID baked in so no ambient session state is needed.
/// </summary>
public sealed class WorkingMemoryTools
{
    private readonly IWorkingMemory _workingMemory;
    private readonly string _sessionId;
    private readonly ILogger _logger;

    public WorkingMemoryTools(IWorkingMemory workingMemory, string sessionId, ILogger logger)
    {
        _workingMemory = workingMemory;
        _sessionId = sessionId;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(SaveToWorkingMemory),
            AIFunctionFactory.Create(GetFromWorkingMemory),
            AIFunctionFactory.Create(ListWorkingMemory)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description("Cache data in working memory (session scratch space) so it can be retrieved " +
                 "in follow-up questions without re-fetching from the external source. " +
                 "Use this after receiving a large payload from any tool to save it temporarily. " +
                 "Choose a descriptive key that summarises what is stored.")]
    public async Task<string> SaveToWorkingMemory(
        [Description("Short descriptive key (e.g. 'calendar_2026-02-18', 'emails_inbox')")] string key,
        [Description("The data to cache — can be a large string, JSON payload, or formatted summary")] string data,
        [Description("How long to keep this data in minutes (default: 5)")] int? ttl_minutes = null)
    {
        _logger.LogInformation("Tool call: SaveToWorkingMemory(key={Key}, ttl={Ttl}min)", key, ttl_minutes);
        var ttl = ttl_minutes.HasValue ? TimeSpan.FromMinutes(ttl_minutes.Value) : (TimeSpan?)null;
        await _workingMemory.SetAsync(_sessionId, key, data, ttl);
        return $"Saved to working memory under key '{key}'.";
    }

    [Description("Retrieve previously cached data from working memory by key. " +
                 "Use when the system context shows an entry in working memory that contains relevant data.")]
    public async Task<string> GetFromWorkingMemory(
        [Description("The key to retrieve (as shown in the working memory list)")] string key)
    {
        _logger.LogInformation("Tool call: GetFromWorkingMemory(key={Key})", key);
        var value = await _workingMemory.GetAsync(_sessionId, key);
        if (value is null)
            return $"Working memory entry '{key}' not found or has expired.";
        return value;
    }

    [Description("List all keys currently in working memory with their expiry times. " +
                 "Use this to discover what cached data is available from earlier in this session.")]
    public async Task<string> ListWorkingMemory()
    {
        _logger.LogInformation("Tool call: ListWorkingMemory()");
        var entries = await _workingMemory.ListAsync(_sessionId);

        if (entries.Count == 0)
            return "Working memory is empty.";

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"Working memory ({entries.Count} entries):");
        foreach (var entry in entries)
        {
            var remaining = entry.ExpiresAt - now;
            var remainingStr = remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                : $"{Math.Max(0, remaining.Seconds)}s";
            sb.AppendLine($"- {entry.Key}: expires in {remainingStr}");
        }
        return sb.ToString().TrimEnd();
    }
}
