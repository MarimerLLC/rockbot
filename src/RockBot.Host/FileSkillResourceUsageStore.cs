using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-backed JSONL implementation of <see cref="ISkillResourceUsageStore"/>.
/// One line per checkout, stored at <c>{profile-base}/skill-resource-usage.jsonl</c>.
/// Mirrors <c>FileWispExecutionLog</c>'s shape — append-only, single-writer
/// semaphore, lazy reads.
/// </summary>
internal sealed class FileSkillResourceUsageStore : ISkillResourceUsageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly ILogger<FileSkillResourceUsageStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileSkillResourceUsageStore(
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileSkillResourceUsageStore> logger)
    {
        var basePath = profileOptions.Value.BasePath;
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(AppContext.BaseDirectory, basePath);
        Directory.CreateDirectory(basePath);
        _filePath = Path.Combine(basePath, "skill-resource-usage.jsonl");
        _logger = logger;
    }

    public async Task RecordCheckoutAsync(
        string skillName, string filename, string sessionId, DateTimeOffset at, CancellationToken ct = default)
    {
        var evt = new SkillResourceCheckoutEvent(skillName, filename, sessionId, at);

        await _writeLock.WaitAsync(ct);
        try
        {
            var line = JsonSerializer.Serialize(evt, JsonOptions);
            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<SkillResourceCheckoutEvent>> QueryCheckoutsAsync(
        string skillName, string filename, DateTimeOffset since, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return [];

        var results = new List<SkillResourceCheckoutEvent>();
        try
        {
            var lines = await File.ReadAllLinesAsync(_filePath, ct);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<SkillResourceCheckoutEvent>(line, JsonOptions);
                    if (evt is null) continue;
                    if (evt.Timestamp < since) continue;
                    if (!string.Equals(evt.SkillName, skillName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(evt.Filename, filename, StringComparison.OrdinalIgnoreCase)) continue;
                    results.Add(evt);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize skill-resource checkout event");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read skill-resource usage log {Path}", _filePath);
        }

        return results.OrderBy(r => r.Timestamp).ToList();
    }
}
