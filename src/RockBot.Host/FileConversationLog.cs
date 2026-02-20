using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// JSONL-based implementation of <see cref="IConversationLog"/>.
/// All turns are appended to a single file: <c>{BasePath}/turns.jsonl</c>.
/// Thread-safe via a single <see cref="SemaphoreSlim"/>.
/// </summary>
internal sealed class FileConversationLog : IConversationLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly ILogger<FileConversationLog> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public FileConversationLog(
        IOptions<ConversationLogOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileConversationLog> logger)
    {
        var basePath = ResolvePath(options.Value.BasePath, profileOptions.Value.BasePath);
        Directory.CreateDirectory(basePath);
        _filePath = Path.Combine(basePath, "turns.jsonl");
        _logger = logger;

        _logger.LogInformation("Conversation log path: {Path}", _filePath);
    }

    public async Task AppendAsync(ConversationLogEntry entry, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, cancellationToken);
            _logger.LogDebug("ConversationLog: appended [{Role}] for session {SessionId}", entry.Role, entry.SessionId);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationLogEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
                return Array.Empty<ConversationLogEntry>();

            var entries = new List<ConversationLogEntry>();
            var lines = await File.ReadAllLinesAsync(_filePath, cancellationToken);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<ConversationLogEntry>(line, JsonOptions);
                    if (entry is not null)
                        entries.Add(entry);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "ConversationLog: failed to deserialize entry from {Path}", _filePath);
                }
            }
            return entries;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
                _logger.LogDebug("ConversationLog: log file cleared");
            }
        }
        finally
        {
            _semaphore.Release();
        }
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
