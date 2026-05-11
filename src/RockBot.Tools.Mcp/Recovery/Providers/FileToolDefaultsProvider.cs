using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Tools.Mcp.Recovery.Providers;

/// <summary>
/// File-backed <see cref="IToolArgumentDefaultsProvider"/> that reads
/// <c>{agent-profile}/tool-defaults/{server}.json</c> files and resolves missing
/// required fields from the configured entries. Used by the Phase 4 closed-loop
/// repair tickets: <see cref="RepairTarget.ToolDefaultRegister"/> writes new
/// defaults here so subsequent recovery calls can fill them deterministically
/// without an LLM.
/// </summary>
/// <remarks>
/// File schema (per server file): a JSON array of entries
/// <c>{providerName, field, value, tool?}</c>. <c>tool</c> is optional —
/// when present, the entry only resolves for that specific tool name; when
/// absent, it resolves for any tool on the server. Files are reloaded
/// automatically when changed on disk via <see cref="FileSystemWatcher"/>.
/// </remarks>
public sealed class FileToolDefaultsProvider : IToolArgumentDefaultsProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _basePath;
    private readonly ILogger<FileToolDefaultsProvider> _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyList<DefaultEntry>> _byServer
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _watcher;

    public FileToolDefaultsProvider(
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileToolDefaultsProvider> logger)
    {
        _logger = logger;
        _basePath = ResolvePath("tool-defaults", profileOptions.Value.BasePath);

        try
        {
            Directory.CreateDirectory(_basePath);
            LoadAllFiles();

            _watcher = new FileSystemWatcher(_basePath, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => SafeReloadFile(e.FullPath);
            _watcher.Changed += (_, e) => SafeReloadFile(e.FullPath);
            _watcher.Deleted += (_, e) => SafeRemoveFile(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                SafeRemoveFile(e.OldFullPath);
                SafeReloadFile(e.FullPath);
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileToolDefaultsProvider: could not initialize at {Path}", _basePath);
        }

        _logger.LogInformation(
            "FileToolDefaultsProvider initialized at {Path} with defaults for {ServerCount} server(s)",
            _basePath, _byServer.Count);
    }

    public bool CanResolve(string serverName, string toolName, string fieldName)
    {
        if (!_byServer.TryGetValue(serverName, out var entries))
            return false;

        foreach (var e in entries)
        {
            if (Matches(e, toolName, fieldName))
                return true;
        }

        return false;
    }

    public Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct)
    {
        if (!_byServer.TryGetValue(ctx.ServerName, out var entries))
            return Task.FromResult<ResolvedDefault?>(null);

        foreach (var e in entries)
        {
            if (!Matches(e, ctx.ToolName, ctx.FieldName))
                continue;

            var value = MaterializeValue(e.Value);
            return Task.FromResult<ResolvedDefault?>(value is null ? null : new ResolvedDefault(value));
        }

        return Task.FromResult<ResolvedDefault?>(null);
    }

    private static bool Matches(DefaultEntry entry, string toolName, string fieldName)
    {
        if (!string.Equals(entry.Field, fieldName, StringComparison.OrdinalIgnoreCase))
            return false;

        // tool is optional — null means "any tool on the server".
        if (string.IsNullOrEmpty(entry.Tool))
            return true;

        return string.Equals(entry.Tool, toolName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Materializes a stored <see cref="JsonElement"/> into a single CLR scalar.
    /// Arrays are no longer supported (fan-out was removed in Amendment 1) and
    /// return <c>null</c> so the provider declines to resolve. Step 5 of the
    /// amendment will tighten this to reject array entries at load time.
    /// </summary>
    internal static object? MaterializeValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => value,
            _ => null,
        };
    }

    private void LoadAllFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            SafeReloadFile(path);
        }
    }

    private void SafeReloadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var server = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(server)) return;

            // Tolerate write-in-progress: brief retry in case the writer hasn't
            // released the file yet.
            string json = string.Empty;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    json = File.ReadAllText(path);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _byServer.TryRemove(server, out _);
                return;
            }

            var entries = JsonSerializer.Deserialize<List<DefaultEntry>>(json, JsonOptions) ?? [];
            _byServer[server] = entries;
            _logger.LogInformation(
                "FileToolDefaultsProvider: loaded {Count} default(s) for server {Server}",
                entries.Count, server);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileToolDefaultsProvider: failed to load {Path}", path);
        }
    }

    private void SafeRemoveFile(string path)
    {
        try
        {
            var server = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(server))
            {
                _byServer.TryRemove(server, out _);
                _logger.LogInformation("FileToolDefaultsProvider: removed defaults for server {Server}", server);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileToolDefaultsProvider: failed to remove {Path}", path);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }

    private static string ResolvePath(string path, string profileBasePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, path);
    }

    internal sealed class DefaultEntry
    {
        public string ProviderName { get; set; } = string.Empty;
        public string? Tool { get; set; }
        public string Field { get; set; } = string.Empty;
        public JsonElement Value { get; set; }
    }
}
