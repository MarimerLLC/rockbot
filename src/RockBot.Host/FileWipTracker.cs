using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Messaging;

namespace RockBot.Host;

/// <summary>
/// File-per-entry WIP tracker. Each in-flight message is persisted as a separate
/// JSON file named <c>{messageId}.json</c> under the configured WIP directory.
/// This gives atomic create/delete semantics without cross-entry locking.
/// </summary>
internal sealed class FileWipTracker : IWipTracker
{
    private readonly string _basePath;
    private readonly ILogger<FileWipTracker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Serialization DTO — <see cref="ReadOnlyMemory{T}"/> is not directly
    /// serializable by System.Text.Json, so the body is stored as base64.
    /// </summary>
    private sealed record PersistedWipEntry(
        string MessageId,
        string MessageType,
        string? CorrelationId,
        string? ReplyTo,
        string Source,
        string? Destination,
        DateTimeOffset MessageTimestamp,
        DateTimeOffset StartedAt,
        Dictionary<string, string> Headers,
        string BodyBase64);

    public FileWipTracker(
        IOptions<WipOptions> wipOptions,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileWipTracker> logger)
    {
        _logger = logger;
        _basePath = ResolvePath(wipOptions.Value.BasePath, profileOptions.Value.BasePath);
        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("WIP tracker persistence path: {Path}", _basePath);
    }

    public async Task<WipEntry> BeginAsync(MessageEnvelope envelope, CancellationToken ct = default)
    {
        var entry = new WipEntry(
            envelope.MessageId,
            envelope.MessageType,
            envelope.CorrelationId,
            envelope.ReplyTo,
            envelope.Source,
            envelope.Destination,
            envelope.Timestamp,
            DateTimeOffset.UtcNow,
            envelope.Headers,
            envelope.Body);

        var persisted = new PersistedWipEntry(
            entry.MessageId,
            entry.MessageType,
            entry.CorrelationId,
            entry.ReplyTo,
            entry.Source,
            entry.Destination,
            entry.MessageTimestamp,
            entry.StartedAt,
            new Dictionary<string, string>(entry.Headers),
            Convert.ToBase64String(entry.Body.Span));

        var path = GetEntryPath(entry.MessageId);
        var json = JsonSerializer.Serialize(persisted, JsonOptions);

        await _lock.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(path, json, ct);
        }
        finally
        {
            _lock.Release();
        }

        HostDiagnostics.WipBegun.Add(1);
        _logger.LogDebug("WIP begun for message {MessageId} type={MessageType}",
            entry.MessageId, entry.MessageType);

        return entry;
    }

    public async Task CompleteAsync(string messageId, CancellationToken ct = default)
    {
        var path = GetEntryPath(messageId);

        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                HostDiagnostics.WipCompleted.Add(1);
                _logger.LogDebug("WIP completed for message {MessageId}", messageId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AbandonAsync(string messageId, string reason, CancellationToken ct = default)
    {
        var path = GetEntryPath(messageId);

        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                HostDiagnostics.WipAbandoned.Add(1);
                _logger.LogWarning("WIP abandoned for message {MessageId}: {Reason}",
                    messageId, reason);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<WipEntry>> GetIncompleteAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_basePath))
            return [];

        var entries = new List<WipEntry>();

        await _lock.WaitAsync(ct);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
                    var persisted = JsonSerializer.Deserialize<PersistedWipEntry>(json, JsonOptions);
                    if (persisted is null) continue;

                    entries.Add(new WipEntry(
                        persisted.MessageId,
                        persisted.MessageType,
                        persisted.CorrelationId,
                        persisted.ReplyTo,
                        persisted.Source,
                        persisted.Destination,
                        persisted.MessageTimestamp,
                        persisted.StartedAt,
                        persisted.Headers,
                        Convert.FromBase64String(persisted.BodyBase64)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read WIP entry from {File}", file);
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        return entries;
    }

    private string GetEntryPath(string messageId) =>
        Path.Combine(_basePath, $"{messageId}.json");

    internal static string ResolvePath(string wipPath, string profileBasePath)
    {
        if (Path.IsPathRooted(wipPath))
            return wipPath;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, wipPath);
    }
}
