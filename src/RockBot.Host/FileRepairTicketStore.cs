using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// PVC-backed <see cref="IRepairTicketStore"/> using one JSON file per ticket
/// (<c>{id}.json</c>) under <see cref="RepairTicketOptions.BasePath"/>. Updates
/// are atomic via temp+rename so a crashed write cannot leave a partial file.
/// See <c>design/self-repair.md</c> Phase 4.
/// </summary>
internal sealed class FileRepairTicketStore : IRepairTicketStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILogger<FileRepairTicketStore> _logger;
    private readonly string _basePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public FileRepairTicketStore(
        IOptions<RepairTicketOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileRepairTicketStore> logger)
    {
        _logger = logger;
        _basePath = ResolvePath(options.Value.BasePath, profileOptions.Value.BasePath);
        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("Repair ticket store path: {Path}", _basePath);
    }

    public async Task<IReadOnlyList<RepairTicket>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tickets = new List<RepairTicket>();
        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            var ticket = await ReadFileAsync(file, cancellationToken);
            if (ticket is not null)
                tickets.Add(ticket);
        }

        return tickets
            .OrderByDescending(t => t.UpdatedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<RepairTicket>> ListOpenAsync(CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken);
        return all
            .Where(t => t.Status is RepairStatus.Open or RepairStatus.InProgress)
            .ToList();
    }

    public async Task<RepairTicket?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        return await ReadFileAsync(path, cancellationToken);
    }

    public async Task SaveAsync(RepairTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (string.IsNullOrWhiteSpace(ticket.Id))
            throw new ArgumentException("RepairTicket.Id is required.", nameof(ticket));

        var path = PathFor(ticket.Id);
        var tmp = path + ".tmp";

        var json = JsonSerializer.Serialize(ticket, JsonOptions);

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(tmp, json, cancellationToken);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = PathFor(id);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private async Task<RepairTicket?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<RepairTicket>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Skipping malformed repair-ticket file {Path}", path);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read repair-ticket file {Path}", path);
            return null;
        }
    }

    private string PathFor(string id) =>
        Path.Combine(_basePath, SanitizeId(id) + ".json");

    private static string SanitizeId(string id)
    {
        // Defensive — caller-supplied ids should already be safe, but reject path
        // traversal and platform-illegal characters here so a bad caller cannot
        // escape the store directory.
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            if (id.Contains(c))
                throw new ArgumentException($"Invalid character in ticket id: {id}", nameof(id));
        }

        if (id.Contains('/') || id.Contains('\\') || id == ".." || id == ".")
            throw new ArgumentException($"Invalid ticket id: {id}", nameof(id));

        return id;
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
}
