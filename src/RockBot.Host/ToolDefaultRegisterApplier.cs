using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Applies a <see cref="RepairTarget.ToolDefaultRegister"/> change by appending
/// a default-value entry to <c>/data/agent/tool-defaults/{server}.json</c>.
/// The Phase 1 file-backed defaults provider reads these files and uses them
/// during mechanical recovery. Idempotent by <c>providerName</c>: re-applying
/// the same provider name overwrites the existing entry rather than duplicating it.
/// </summary>
internal sealed class ToolDefaultRegisterApplier : IRepairTargetApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILogger<ToolDefaultRegisterApplier> _logger;
    private readonly string _basePath;

    public ToolDefaultRegisterApplier(
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<ToolDefaultRegisterApplier> logger)
    {
        _logger = logger;
        _basePath = ResolvePath("tool-defaults", profileOptions.Value.BasePath);
    }

    public RepairTarget Target => RepairTarget.ToolDefaultRegister;

    public async Task<RepairApplyOutcome> ApplyAsync(RepairTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var change = ticket.Change.Deserialize<ToolDefaultRegisterChange>(JsonOptions)
            ?? throw new ArgumentException("ToolDefaultRegister change is empty.", nameof(ticket));

        if (string.IsNullOrWhiteSpace(change.Server))
            throw new ArgumentException("ToolDefaultRegister change missing 'server'.", nameof(ticket));
        if (string.IsNullOrWhiteSpace(change.ProviderName))
            throw new ArgumentException("ToolDefaultRegister change missing 'providerName'.", nameof(ticket));
        if (string.IsNullOrWhiteSpace(change.Field))
            throw new ArgumentException("ToolDefaultRegister change missing 'field'.", nameof(ticket));
        if (change.Value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("ToolDefaultRegister change missing 'value'.", nameof(ticket));

        Directory.CreateDirectory(_basePath);
        var filePath = Path.Combine(_basePath, change.Server + ".json");

        var entries = await ReadExistingAsync(filePath, cancellationToken);

        // Dedup by providerName.
        var existingIndex = entries.FindIndex(e =>
            string.Equals(e.ProviderName, change.ProviderName, StringComparison.OrdinalIgnoreCase));

        var entry = new ToolDefaultEntry
        {
            ProviderName = change.ProviderName!,
            Tool = change.Tool,
            Field = change.Field!,
            Value = change.Value,
        };

        var action = existingIndex >= 0 ? "replaced" : "appended";
        if (existingIndex >= 0)
            entries[existingIndex] = entry;
        else
            entries.Add(entry);

        await WriteAtomicAsync(filePath, entries, cancellationToken);

        var diff = JsonSerializer.SerializeToElement(new
        {
            server = change.Server,
            providerName = change.ProviderName,
            field = change.Field,
            tool = change.Tool,
            action,
            entryCount = entries.Count,
        }, JsonOptions);

        _logger.LogInformation(
            "ToolDefaultRegisterApplier: {Action} provider {Provider} for {Server}/{Field}",
            action, change.ProviderName, change.Server, change.Field);

        return new RepairApplyOutcome(diff, Revert: null);
    }

    private static async Task<List<ToolDefaultEntry>> ReadExistingAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return [];

        var json = await File.ReadAllTextAsync(filePath, ct);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<ToolDefaultEntry>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // Malformed file — treat as empty so we don't lose the new entry, but
            // we deliberately rewrite the file on save which discards the corrupt content.
            return [];
        }
    }

    private static async Task WriteAtomicAsync(string filePath, List<ToolDefaultEntry> entries, CancellationToken ct)
    {
        var tmp = filePath + ".tmp";
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, filePath, overwrite: true);
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

    internal sealed class ToolDefaultRegisterChange
    {
        public string? Server { get; set; }
        public string? ProviderName { get; set; }
        public string? Field { get; set; }
        public string? Tool { get; set; }
        public JsonElement Value { get; set; }
    }

    internal sealed class ToolDefaultEntry
    {
        public string ProviderName { get; set; } = string.Empty;
        public string? Tool { get; set; }
        public string Field { get; set; } = string.Empty;
        public JsonElement Value { get; set; }
    }
}
