using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-based skill usage store. Each session's invocation events are appended to a separate JSONL file:
/// <c>{basePath}/{sessionId}.jsonl</c>. One JSON object per line.
/// </summary>
internal sealed class FileSkillUsageStore : ISkillUsageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _basePath;
    private readonly ILogger<FileSkillUsageStore> _logger;

    /// <summary>Per-session semaphores prevent concurrent writes racing on the same file.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    public FileSkillUsageStore(
        IOptions<SkillOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileSkillUsageStore> logger)
    {
        _basePath = ResolvePath(options.Value.UsageBasePath, profileOptions.Value.BasePath);
        _logger = logger;

        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("Skill usage store path: {Path}", _basePath);
    }

    public async Task AppendAsync(SkillInvocationEvent evt, CancellationToken ct = default)
    {
        var sem = _writeLocks.GetOrAdd(evt.SessionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_basePath, $"{evt.SessionId}.jsonl");
            var line = JsonSerializer.Serialize(evt, JsonOptions);
            await File.AppendAllTextAsync(path, line + Environment.NewLine, ct);

            _logger.LogDebug("Skill usage appended [{SkillName}] for session {SessionId}", evt.SkillName, evt.SessionId);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<IReadOnlyList<SkillInvocationEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        var path = Path.Combine(_basePath, $"{sessionId}.jsonl");
        if (!File.Exists(path))
            return Array.Empty<SkillInvocationEvent>();

        return await ReadEventsAsync(path, ct);
    }

    public async Task<IReadOnlyList<SkillInvocationEvent>> QueryRecentAsync(
        DateTimeOffset since,
        int maxResults,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_basePath))
            return Array.Empty<SkillInvocationEvent>();

        var results = new List<SkillInvocationEvent>();

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.jsonl"))
        {
            ct.ThrowIfCancellationRequested();

            var events = await ReadEventsAsync(file, ct);
            results.AddRange(events.Where(e => e.Timestamp >= since));
        }

        return results
            .OrderBy(e => e.Timestamp)
            .Take(maxResults)
            .ToList();
    }

    private async Task<IReadOnlyList<SkillInvocationEvent>> ReadEventsAsync(string path, CancellationToken ct)
    {
        var events = new List<SkillInvocationEvent>();
        try
        {
            var lines = await File.ReadAllLinesAsync(path, ct);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<SkillInvocationEvent>(line, JsonOptions);
                    if (evt is not null)
                        events.Add(evt);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize skill usage event from {Path}", path);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read skill usage file {Path}", path);
        }
        return events;
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
