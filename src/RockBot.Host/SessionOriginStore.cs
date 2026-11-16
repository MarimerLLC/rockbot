using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Cache of each user session's origin (channel + first-prompt summary + start time), written on
/// every inbound <see cref="UserMessage"/> arrival. Read by entry points that emit
/// <b>unsolicited</b> replies for a session but don't have the originating
/// <see cref="UserMessage"/> in scope — subagent result/progress handlers and A2A
/// receive-side handlers — so they can stamp <see cref="AgentReply.Origin"/> and let
/// frontends anchor the message to the request that started it.
/// <para>
/// Parallels <see cref="SessionClientCapabilityStore"/> in lifetime and intent: a singleton
/// spanning the agent process, persisting across loops (a subagent/A2A callback may fire long
/// after the user's loop completes), cleared on session reset. First-writer-wins per session
/// so follow-up turns don't overwrite the anchor of in-flight background work.
/// </para>
/// <para>
/// With a file path (the agent's registration: <see cref="FileName"/> under the profile
/// directory), the cache also survives a restart (#841), so a result that lands after one is
/// still anchored. The file is rewritten only when an entry is added or removed, which is rare,
/// and is pruned to entries started in the last <see cref="MaxAge"/>, at most
/// <see cref="MaxEntries"/> of them. Without a path it lives only in memory.
/// </para>
/// </summary>
public sealed class SessionOriginStore
{
    /// <summary>The file's name under the agent profile directory.</summary>
    public const string FileName = "session-origins.json";

    /// <summary>The persisted file keeps at most this many sessions, dropping the oldest first.</summary>
    internal const int MaxEntries = 1000;

    /// <summary>The persisted file drops a session whose work started longer ago than this.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly Dictionary<string, ReplyOrigin> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>An in-memory store, lost on restart.</summary>
    public SessionOriginStore()
    {
        _logger = NullLogger.Instance;
        _time = TimeProvider.System;
    }

    /// <summary>A store kept in <paramref name="path"/>, loaded from it now.</summary>
    public SessionOriginStore(string path, ILogger<SessionOriginStore>? logger = null, TimeProvider? timeProvider = null)
    {
        _path = path;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        Load();
    }

    /// <summary>
    /// Records the origin for <paramref name="sessionId"/> if none is cached yet. The first
    /// turn of a session establishes the anchor; later turns within the same session do not
    /// overwrite it, so background work started early still reports its true beginning.
    /// </summary>
    public void Set(string sessionId, ReplyOrigin origin)
    {
        lock (_lock)
        {
            if (_byId.TryAdd(sessionId, origin))
                Save();
        }
    }

    /// <summary>Returns the cached origin for <paramref name="sessionId"/>, or null.</summary>
    public ReplyOrigin? Get(string sessionId)
    {
        lock (_lock)
            return _byId.GetValueOrDefault(sessionId);
    }

    /// <summary>Removes any cached entry. Called when the user resets the conversation.</summary>
    public void Clear(string sessionId)
    {
        lock (_lock)
        {
            if (_byId.Remove(sessionId))
                Save();
        }
    }

    /// <summary>The file's contents.</summary>
    internal sealed class OriginsFile
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public Dictionary<string, ReplyOrigin> Origins { get; set; } = [];
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
            return;

        try
        {
            var file = JsonSerializer.Deserialize<OriginsFile>(File.ReadAllText(_path), JsonOptions);
            foreach (var (sessionId, origin) in file?.Origins ?? [])
            {
                if (origin is not null)
                    _byId.TryAdd(sessionId, origin);
            }
            Prune();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Anchors are a convenience; a bad file is set aside rather than failing startup.
            _logger.LogWarning(ex, "Could not read session origins from {Path}; starting empty", _path);
            _byId.Clear();
            try { File.Move(_path, _path + ".corrupt", overwrite: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Writes the cache to the file. Called under <see cref="_lock"/>, so writes are in order.</summary>
    private void Save()
    {
        if (_path is null)
            return;

        Prune();
        try
        {
            var json = JsonSerializer.Serialize(new OriginsFile { Origins = new(_byId, StringComparer.Ordinal) }, JsonOptions);
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory cache still answers; only a restart before the next write loses it.
            _logger.LogWarning(ex, "Could not write session origins to {Path}", _path);
        }
    }

    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - MaxAge;
        foreach (var (sessionId, _) in _byId.Where(e => e.Value.StartedAt < cutoff).ToList())
            _byId.Remove(sessionId);

        if (_byId.Count > MaxEntries)
        {
            foreach (var (sessionId, _) in _byId.OrderBy(e => e.Value.StartedAt).Take(_byId.Count - MaxEntries).ToList())
                _byId.Remove(sessionId);
        }
    }
}
