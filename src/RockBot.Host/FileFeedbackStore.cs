using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-based feedback store. Each session's entries are appended to a separate JSONL file:
/// <c>{basePath}/{sessionId}.jsonl</c>. One JSON object per line.
/// </summary>
internal sealed class FileFeedbackStore : IFeedbackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _basePath;
    private readonly ILogger<FileFeedbackStore> _logger;

    /// <summary>Per-session semaphores prevent concurrent writes racing on the same file.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    public FileFeedbackStore(
        IOptions<FeedbackOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileFeedbackStore> logger)
    {
        _basePath = ResolvePath(options.Value.BasePath, profileOptions.Value.BasePath);
        _logger = logger;

        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("Feedback store path: {Path}", _basePath);
    }

    public async Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default)
    {
        var sem = _writeLocks.GetOrAdd(entry.SessionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(_basePath, $"{entry.SessionId}.jsonl");
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);

            _logger.LogDebug("Feedback appended [{SignalType}] for session {SessionId}", entry.SignalType, entry.SessionId);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_basePath, $"{sessionId}.jsonl");
        if (!File.Exists(path))
            return Array.Empty<FeedbackEntry>();

        return await ReadEntriesAsync(path, cancellationToken);
    }

    public async Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(
        DateTimeOffset since,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_basePath))
            return Array.Empty<FeedbackEntry>();

        var results = new List<FeedbackEntry>();

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = await ReadEntriesAsync(file, cancellationToken);
            results.AddRange(entries.Where(e => e.Timestamp >= since));
        }

        return results
            .OrderBy(e => e.Timestamp)
            .Take(maxResults)
            .ToList();
    }

    private async Task<IReadOnlyList<FeedbackEntry>> ReadEntriesAsync(string path, CancellationToken cancellationToken)
    {
        var entries = new List<FeedbackEntry>();
        try
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<FeedbackEntry>(line, JsonOptions);
                    if (entry is not null)
                        entries.Add(entry);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize feedback entry from {Path}", path);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read feedback file {Path}", path);
        }
        return entries;
    }

    private static string ResolvePath(string path, string basePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(AppContext.BaseDirectory, basePath);

        return Path.Combine(baseDir, path);
    }
}
