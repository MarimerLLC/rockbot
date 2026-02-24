using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Singleton. Appends tier-routing decisions to <c>{BasePath}/tier-routing-log.jsonl</c>
/// (capped at 200 entries) and reads them back for the dream self-correction pass.
/// </summary>
public sealed class TierRoutingLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger<TierRoutingLogger> _logger;

    public TierRoutingLogger(
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<TierRoutingLogger> logger)
    {
        var basePath = profileOptions.Value.BasePath;
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(AppContext.BaseDirectory, basePath);

        _filePath = Path.Combine(basePath, "tier-routing-log.jsonl");
        _logger = logger;
    }

    /// <summary>
    /// Appends a routing entry. Keeps at most 200 lines total (oldest evicted).
    /// Fire-and-forget safe: exceptions are caught and logged.
    /// </summary>
    public async Task AppendAsync(TierRoutingEntry entry)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var newLine = JsonSerializer.Serialize(entry, JsonOptions);

            string[] existingLines = [];
            if (File.Exists(_filePath))
            {
                existingLines = await File.ReadAllLinesAsync(_filePath).ConfigureAwait(false);
                existingLines = existingLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            }

            // Keep last 199 non-empty lines + append new line = max 200 total
            var linesToKeep = existingLines.Length >= 199
                ? existingLines[^199..]
                : existingLines;

            var allLines = linesToKeep.Append(newLine);
            await File.WriteAllLinesAsync(_filePath, allLines).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TierRoutingLogger: failed to append entry");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads recent routing entries, newest last. Returns an empty list if the file does not exist.
    /// </summary>
    public async Task<IReadOnlyList<TierRoutingEntry>> ReadRecentAsync(int maxResults = 200)
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            var lines = await File.ReadAllLinesAsync(_filePath).ConfigureAwait(false);
            var entries = new List<TierRoutingEntry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<TierRoutingEntry>(line, JsonOptions);
                    if (entry is not null)
                        entries.Add(entry);
                }
                catch (JsonException) { /* skip malformed lines */ }
            }
            return entries.TakeLast(maxResults).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TierRoutingLogger: failed to read log");
            return [];
        }
    }
}
