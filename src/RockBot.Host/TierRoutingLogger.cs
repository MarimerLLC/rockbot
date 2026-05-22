using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Singleton. Appends tier-routing decisions to <c>{BasePath}/tier-routing-log.jsonl</c>
/// and reads them back for the dream self-correction pass and the introspection MCP server.
/// The retention cap is controlled by <see cref="AgentProfileOptions.TierRoutingLogMaxEntries"/>
/// (default 1500); on append the oldest entries are trimmed once the cap is reached.
/// </summary>
public sealed class TierRoutingLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly int _maxEntries;
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
        _maxEntries = Math.Max(1, profileOptions.Value.TierRoutingLogMaxEntries);
        _logger = logger;
    }

    /// <summary>
    /// Appends a routing entry. Keeps at most <see cref="AgentProfileOptions.TierRoutingLogMaxEntries"/>
    /// lines total (oldest evicted). Fire-and-forget safe: exceptions are caught and logged.
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

            // Keep last (maxEntries - 1) non-empty lines + append new line = at most maxEntries total
            var keepCount = _maxEntries - 1;
            var linesToKeep = existingLines.Length > keepCount
                ? existingLines[^keepCount..]
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
