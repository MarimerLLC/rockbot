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
    private readonly ISkillStore? _skillStore;
    private readonly ILlmClient _llmClient;
    private readonly DreamOptions _options;
    private readonly AgentProfileOptions _profileOptions;
    private readonly ILogger<DreamService> _logger;
    private Timer? _timer;
    private string? _dreamDirective;
    private string? _skillDreamDirective;

    public DreamService(
        ILongTermMemory memory,
        IEnumerable<ISkillStore> skillStores,
        ILlmClient llmClient,
        IOptions<DreamOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<DreamService> logger)
    {
        _memory = memory;
        _skillStore = skillStores.FirstOrDefault();
        _llmClient = llmClient;
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

        if (_skillStore is not null)
        {
            var skillDirectivePath = ResolvePath(_options.SkillDirectivePath, _profileOptions.BasePath);
            _skillDreamDirective = File.Exists(skillDirectivePath)
                ? File.ReadAllText(skillDirectivePath)
                : BuiltInSkillDirective;

            if (!File.Exists(skillDirectivePath))
                _logger.LogWarning("DreamService: skill directive not found at {Path}; using built-in fallback", skillDirectivePath);
            else
                _logger.LogInformation("DreamService: loaded skill directive from {Path}", skillDirectivePath);
        }

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
            // Dream is low-priority. If another LLM call is in flight, back off and
            // retry rather than queueing immediately behind an active user request.
            while (!_llmClient.IsIdle)
            {
                _logger.LogDebug("DreamService: LLM busy, delaying dream cycle by 5s");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

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

            var response = await _llmClient.GetResponseAsync(messages, new ChatOptions());
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

            if (_skillStore is not null)
                await ConsolidateSkillsAsync();

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

    private async Task ConsolidateSkillsAsync()
    {
        var all = await _skillStore!.ListAsync();

        // Prune skills that have not been used in 18 months
        var threshold = DateTimeOffset.UtcNow.AddMonths(-18);
        var pruned = 0;
        foreach (var skill in all)
        {
            var lastActivity = skill.LastUsedAt ?? skill.UpdatedAt ?? skill.CreatedAt;
            if (lastActivity < threshold)
            {
                await _skillStore!.DeleteAsync(skill.Name);
                pruned++;
                _logger.LogInformation(
                    "DreamService: pruned stale skill '{Name}' (last activity: {Date})",
                    skill.Name, lastActivity);
            }
        }

        if (pruned > 0)
            all = await _skillStore!.ListAsync();

        if (all.Count < 2)
        {
            _logger.LogInformation(
                "DreamService: only {Count} skill(s) — nothing to consolidate; skipping",
                all.Count);
            return;
        }

        _logger.LogDebug("DreamService: fetched {Count} skills for consolidation", all.Count);

        var userMessage = new StringBuilder();
        userMessage.AppendLine($"The agent currently has {all.Count} skills. Consolidate them:");
        userMessage.AppendLine();

        for (var i = 0; i < all.Count; i++)
        {
            var s = all[i];
            userMessage.AppendLine($"{i + 1}. [NAME:{s.Name}] summary: {s.Summary}");
            userMessage.AppendLine(s.Content);
            userMessage.AppendLine();
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _skillDreamDirective!),
            new(ChatRole.User, userMessage.ToString())
        };

        var response = await _llmClient.GetResponseAsync(messages, new ChatOptions());
        var raw = response.Text?.Trim() ?? string.Empty;
        var json = ExtractJsonObject(raw);

        if (string.IsNullOrEmpty(json))
        {
            _logger.LogWarning("DreamService: skill LLM returned no parseable JSON; skipping skill consolidation");
            return;
        }

        _logger.LogDebug("DreamService: skill LLM response JSON ({Length} chars): {Json}", json.Length, json);

        var result = JsonSerializer.Deserialize<SkillDreamResultDto>(json, JsonOptions);
        if (result is null)
        {
            _logger.LogWarning("DreamService: failed to deserialize skill dream result; skipping");
            return;
        }

        var deleted = 0;
        var saved = 0;

        // Union of explicit toDelete names and all sourceNames referenced by saved skills.
        // Mirrors the exhaustive-deletion contract used for memory consolidation.
        var allToDelete = new HashSet<string>(
            result.ToDelete ?? [],
            StringComparer.OrdinalIgnoreCase);

        foreach (var dto in result.ToSave ?? [])
            foreach (var srcName in dto.SourceNames ?? [])
                allToDelete.Add(srcName);

        foreach (var name in allToDelete)
        {
            await _skillStore!.DeleteAsync(name);
            deleted++;
            _logger.LogDebug("DreamService: deleted skill '{Name}'", name);
        }

        // Carry forward the earliest CreatedAt and most recent LastUsedAt from merged source skills
        var createdAtByName = all.ToDictionary(
            s => s.Name, s => s.CreatedAt,
            StringComparer.OrdinalIgnoreCase);

        var lastUsedAtByName = all.ToDictionary(
            s => s.Name, s => s.LastUsedAt,
            StringComparer.OrdinalIgnoreCase);

        foreach (var dto in result.ToSave ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Content))
                continue;

            var sourceNames = dto.SourceNames ?? [];
            var minCreatedAt = sourceNames.Count > 0
                ? sourceNames
                    .Where(createdAtByName.ContainsKey)
                    .Select(n => createdAtByName[n])
                    .DefaultIfEmpty(DateTimeOffset.UtcNow)
                    .Min()
                : DateTimeOffset.UtcNow;

            var maxLastUsedAt = sourceNames.Count > 0
                ? sourceNames
                    .Where(n => lastUsedAtByName.ContainsKey(n) && lastUsedAtByName[n].HasValue)
                    .Select(n => lastUsedAtByName[n])
                    .DefaultIfEmpty(null)
                    .Max()
                : null;

            var skill = new Skill(
                Name: dto.Name.Trim(),
                Summary: dto.Summary?.Trim() ?? string.Empty,
                Content: dto.Content.Trim(),
                CreatedAt: minCreatedAt,
                UpdatedAt: DateTimeOffset.UtcNow,
                LastUsedAt: maxLastUsedAt);

            await _skillStore!.SaveAsync(skill);
            saved++;
            _logger.LogDebug("DreamService: saved merged skill '{Name}'", skill.Name);
        }

        _logger.LogInformation(
            "DreamService: skill consolidation complete — {Deleted} deleted, {Saved} saved",
            deleted, saved);
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

    private const string BuiltInSkillDirective = """
        You are a skill consolidation assistant. Review all skill documents for semantic overlap or near-duplication.
        Merge overlapping skills into improved combined ones and return structured JSON:
        { "toDelete": [...names to remove...], "toSave": [...new/merged skills...] }
        Each skill in toSave: { "name", "summary", "content", "sourceNames" }
        The summary must be one sentence of 15 words or fewer.
        Every name in any sourceNames list must also appear in toDelete.
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

    private sealed record SkillDreamResultDto(
        List<string>? ToDelete,
        List<SkillDreamEntryDto>? ToSave);

    private sealed record SkillDreamEntryDto(
        string Name,
        string? Summary,
        string Content,
        IReadOnlyList<string>? SourceNames);
}
