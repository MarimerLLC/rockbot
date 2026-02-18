using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Periodic background service that consolidates the agent's long-term memory corpus —
/// finding duplicates, merging them into better entries, refining categories, and pruning noise.
/// </summary>
internal sealed class DreamService : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ILongTermMemory _memory;
    private readonly IChatClient _chatClient;
    private readonly DreamOptions _options;
    private readonly AgentProfileOptions _profileOptions;
    private readonly ILogger<DreamService> _logger;
    private Timer? _timer;
    private string? _dreamDirective;

    public DreamService(
        ILongTermMemory memory,
        IChatClient chatClient,
        IOptions<DreamOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<DreamService> logger)
    {
        _memory = memory;
        _chatClient = chatClient;
        _options = options.Value;
        _profileOptions = profileOptions.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("DreamService: dreaming is disabled; skipping timer setup");
            return Task.CompletedTask;
        }

        // Load shared memory rules (if present) to prepend to the dream directive
        var memoryRulesPath = ResolvePath("memory-rules.md", _profileOptions.BasePath);
        var memoryRules = File.Exists(memoryRulesPath) ? File.ReadAllText(memoryRulesPath) : string.Empty;
        if (!string.IsNullOrEmpty(memoryRules))
            _logger.LogInformation("DreamService: loaded memory-rules from {Path}", memoryRulesPath);

        var directivePath = ResolvePath(_options.DirectivePath, _profileOptions.BasePath);
        var dreamDirective = File.Exists(directivePath)
            ? File.ReadAllText(directivePath)
            : BuiltInDirective;

        if (!File.Exists(directivePath))
            _logger.LogWarning("DreamService: dream directive not found at {Path}; using built-in fallback", directivePath);
        else
            _logger.LogInformation("DreamService: loaded dream directive from {Path}", directivePath);

        _dreamDirective = string.IsNullOrEmpty(memoryRules)
            ? dreamDirective
            : memoryRules + "\n\n---\n\n" + dreamDirective;

        _timer = new Timer(
            state => { _ = DreamAsync(); },
            null,
            _options.InitialDelay,
            _options.Interval);

        _logger.LogInformation(
            "DreamService: scheduled — first cycle in {InitialDelay}, then every {Interval}",
            _options.InitialDelay, _options.Interval);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private async Task DreamAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("DreamService: dream cycle starting");

        try
        {
            var all = await _memory.SearchAsync(new MemorySearchCriteria(MaxResults: 1000));

            if (all.Count < 2)
            {
                _logger.LogInformation(
                    "DreamService: only {Count} memory entries — nothing to consolidate; skipping",
                    all.Count);
                return;
            }

            _logger.LogDebug("DreamService: fetched {Count} memory entries for consolidation", all.Count);

            // Build user message: numbered list with IDs, categories, tags, content
            var userMessage = new StringBuilder();
            userMessage.AppendLine($"The agent currently has {all.Count} memory entries. Consolidate them:");
            userMessage.AppendLine();

            for (var i = 0; i < all.Count; i++)
            {
                var e = all[i];
                var tags = e.Tags.Count > 0 ? string.Join(", ", e.Tags) : "(none)";
                userMessage.AppendLine($"{i + 1}. [ID:{e.Id}] category={e.Category ?? "uncategorized"} tags=[{tags}]");
                userMessage.AppendLine($"   {e.Content}");
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _dreamDirective!),
                new(ChatRole.User, userMessage.ToString())
            };

            var response = await _chatClient.GetResponseAsync(messages, new ChatOptions());
            var raw = response.Text?.Trim() ?? string.Empty;
            var json = ExtractJsonObject(raw);

            if (string.IsNullOrEmpty(json))
            {
                _logger.LogWarning("DreamService: LLM returned no parseable JSON object; skipping cycle");
                return;
            }

            _logger.LogDebug("DreamService: LLM response JSON ({Length} chars): {Json}", json.Length, json);

            var result = JsonSerializer.Deserialize<DreamResultDto>(json, JsonOptions);
            if (result is null)
            {
                _logger.LogWarning("DreamService: failed to deserialize dream result; skipping cycle");
                return;
            }

            var deleted = 0;
            var saved = 0;

            // Union of explicit toDelete IDs and all sourceIds referenced by saved entries.
            // This enforces the exhaustive-deletion contract even when the LLM omits some IDs
            // from toDelete while still listing them in sourceIds.
            var allToDelete = new HashSet<string>(
                result.ToDelete ?? [],
                StringComparer.OrdinalIgnoreCase);

            foreach (var dto in result.ToSave ?? [])
                foreach (var srcId in dto.SourceIds ?? [])
                    allToDelete.Add(srcId);

            foreach (var id in allToDelete)
            {
                await _memory.DeleteAsync(id);
                deleted++;
                _logger.LogDebug("DreamService: deleted entry {Id}", id);
            }

            // Build lookup of source CreatedAt values to carry forward min date for merged entries
            var createdAtById = all.ToDictionary(e => e.Id, e => e.CreatedAt);

            foreach (var dto in result.ToSave ?? [])
            {
                if (string.IsNullOrWhiteSpace(dto.Content))
                    continue;

                // Carry forward min CreatedAt from referenced source IDs; fallback to now
                var sourceIds = dto.SourceIds ?? [];
                var minCreatedAt = sourceIds.Count > 0
                    ? sourceIds
                        .Where(createdAtById.ContainsKey)
                        .Select(id => createdAtById[id])
                        .DefaultIfEmpty(DateTimeOffset.UtcNow)
                        .Min()
                    : DateTimeOffset.UtcNow;

                var entry = new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    Content: dto.Content.Trim(),
                    Category: string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category.Trim(),
                    Tags: dto.Tags ?? [],
                    CreatedAt: minCreatedAt,
                    UpdatedAt: DateTimeOffset.UtcNow);

                await _memory.SaveAsync(entry);
                saved++;
                _logger.LogDebug("DreamService: saved entry {Id} ({Category}): {Content}",
                    entry.Id, entry.Category ?? "(none)", entry.Content);
            }

            sw.Stop();
            _logger.LogInformation(
                "DreamService: dream cycle complete — {Deleted} deleted, {Saved} saved, elapsed {Elapsed}",
                deleted, saved, sw.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DreamService: dream cycle failed");
        }
    }

    /// <summary>
    /// Extracts the outermost JSON object from <paramref name="text"/>, tolerating
    /// DeepSeek-style thinking blocks and prose preamble.
    /// </summary>
    private static string ExtractJsonObject(string text)
    {
        // Strip <think>...</think> blocks first (DeepSeek reasoning preamble)
        var thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        var thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkStart >= 0 && thinkEnd > thinkStart)
            text = text[(thinkEnd + "</think>".Length)..].TrimStart();

        var objStart = text.IndexOf('{');
        var objEnd = text.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
            return text[objStart..(objEnd + 1)];

        return string.Empty;
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

    private const string BuiltInDirective = """
        You are a memory consolidation assistant. Review all memory entries for duplicates
        or near-duplicates. Merge them into improved entries and return structured JSON:
        { "toDelete": [...IDs to remove...], "toSave": [...new/merged entries...] }
        Each entry in toSave: { "content", "category", "tags", "sourceIds" }
        If nothing needs consolidation, return: { "toDelete": [], "toSave": [] }
        """;

    private sealed record DreamResultDto(
        List<string>? ToDelete,
        List<DreamEntryDto>? ToSave);

    private sealed record DreamEntryDto(
        string Content,
        string? Category,
        IReadOnlyList<string>? Tags,
        IReadOnlyList<string>? SourceIds);
}
