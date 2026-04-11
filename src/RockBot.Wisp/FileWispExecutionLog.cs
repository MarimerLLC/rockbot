using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Wisp;

/// <summary>
/// File-based wisp execution log. All records are appended to a single JSONL file:
/// <c>{basePath}/wisp-executions.jsonl</c>. One JSON object per line.
/// </summary>
internal sealed class FileWispExecutionLog : IWispExecutionLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly ILogger<FileWispExecutionLog> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileWispExecutionLog(WispOptions options, ILogger<FileWispExecutionLog> logger)
    {
        var basePath = options.SharedVolumePath ?? Path.Combine(AppContext.BaseDirectory, "wisp-log");
        Directory.CreateDirectory(basePath);
        _filePath = Path.Combine(basePath, "wisp-executions.jsonl");
        _logger = logger;
    }

    public async Task AppendAsync(WispExecutionRecord record, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOptions);
            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, ct);
            _logger.LogDebug("Wisp execution logged [{WispId}] success={Success}", record.WispId, record.Succeeded);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
        DateTimeOffset since, int maxResults, CancellationToken ct = default)
    {
        var records = await ReadAllAsync(ct);
        return records
            .Where(r => r.Timestamp >= since)
            .OrderBy(r => r.Timestamp)
            .Take(maxResults)
            .ToList();
    }

    public async Task<WispExecutionRecord?> FindRecentFailureAsync(
        string definitionHash, string? sessionId, CancellationToken ct = default)
    {
        var records = await ReadAllAsync(ct);

        // Find the most recent failed record with the same definition hash,
        // preferring records from the same session
        return records
            .Where(r => !r.Succeeded && r.DefinitionHash == definitionHash)
            .OrderByDescending(r => r.SessionId == sessionId ? 1 : 0)
            .ThenByDescending(r => r.Timestamp)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<WispExecutionRecord>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return [];

        var records = new List<WispExecutionRecord>();
        try
        {
            var lines = await File.ReadAllLinesAsync(_filePath, ct);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<WispExecutionRecord>(line, JsonOptions);
                    if (record is not null)
                        records.Add(record);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize wisp execution record");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read wisp execution log file {Path}", _filePath);
        }
        return records;
    }
}
