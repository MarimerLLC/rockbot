using System.Text.Json;
using System.Text.Json.Serialization;

namespace RockBot.Llm.Copilot;

/// <summary>
/// Tracks cumulative Copilot usage metrics and persists them to a JSON file
/// on the shared data volume so other services (e.g. introspection MCP) can read them.
/// Thread-safe; updated after each Copilot session completes.
/// </summary>
public sealed class CopilotUsageTracker
{
    private readonly string _filePath;
    private readonly Lock _lock = new();
    private CopilotUsageSnapshot _snapshot = new();

    public CopilotUsageTracker(string filePath)
    {
        _filePath = filePath;

        // Load existing snapshot if present (survives agent restarts).
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                _snapshot = JsonSerializer.Deserialize<CopilotUsageSnapshot>(json) ?? new();
            }
            catch
            {
                _snapshot = new();
            }
        }
    }

    /// <summary>
    /// Records a single LLM call (premium interaction) from an AssistantUsageEvent.
    /// </summary>
    public void RecordLlmCall(
        string? model, double? inputTokens, double? outputTokens,
        double? cost, double? durationMs)
    {
        lock (_lock)
        {
            _snapshot.PremiumRequests++;
            _snapshot.TotalInputTokens += (long)(inputTokens ?? 0);
            _snapshot.TotalOutputTokens += (long)(outputTokens ?? 0);
            _snapshot.TotalCostMultiplier += cost ?? 0;
            _snapshot.LastModel = model;
            _snapshot.LastUpdated = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Records a session creation.
    /// </summary>
    public void RecordSession()
    {
        lock (_lock)
        {
            _snapshot.SessionsCreated++;
        }
    }

    /// <summary>
    /// Records a rate-limit retry.
    /// </summary>
    public void RecordRateLimit()
    {
        lock (_lock)
        {
            _snapshot.RateLimitRetries++;
        }
    }

    /// <summary>
    /// Flushes the current snapshot to disk. Called after each session completes.
    /// </summary>
    public void Flush()
    {
        CopilotUsageSnapshot copy;
        lock (_lock)
        {
            copy = _snapshot with { }; // Snapshot for serialization
        }

        var json = JsonSerializer.Serialize(copy, CopilotUsageJsonContext.Default.CopilotUsageSnapshot);

        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_filePath, json);
    }
}

/// <summary>
/// Serializable snapshot of cumulative Copilot usage metrics.
/// </summary>
public sealed record CopilotUsageSnapshot
{
    public long SessionsCreated { get; set; }
    public long PremiumRequests { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public double TotalCostMultiplier { get; set; }
    public long RateLimitRetries { get; set; }
    public string? LastModel { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
}

[JsonSerializable(typeof(CopilotUsageSnapshot))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CopilotUsageJsonContext : JsonSerializerContext;
