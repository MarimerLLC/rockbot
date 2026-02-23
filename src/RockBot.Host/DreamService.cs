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
    private readonly IFeedbackStore? _feedbackStore;
    private readonly ISkillUsageStore? _skillUsageStore;
    private readonly IConversationLog? _conversationLog;
    private readonly ILlmClient _llmClient;
    private readonly IUserActivityMonitor _userActivityMonitor;
    private readonly DreamOptions _options;
    private readonly AgentProfileOptions _profileOptions;
    private readonly ILogger<DreamService> _logger;
    private Timer? _timer;
    private string? _dreamDirective;
    private string? _skillDreamDirective;
    private string? _skillOptimizeDirective;
    private string? _prefDreamDirective;
    private string? _skillGapDirective;

    public DreamService(
        ILongTermMemory memory,
        IEnumerable<ISkillStore> skillStores,
        ILlmClient llmClient,
        IUserActivityMonitor userActivityMonitor,
        IOptions<DreamOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<DreamService> logger,
        IFeedbackStore? feedbackStore = null,
        ISkillUsageStore? skillUsageStore = null,
        IConversationLog? conversationLog = null)
    {
        _memory = memory;
        _skillStore = skillStores.FirstOrDefault();
        _feedbackStore = feedbackStore;
        _skillUsageStore = skillUsageStore;
        _conversationLog = conversationLog;
        _llmClient = llmClient;
        _userActivityMonitor = userActivityMonitor;
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

            var skillOptimizeDirectivePath = ResolvePath(_options.SkillOptimizeDirectivePath, _profileOptions.BasePath);
            _skillOptimizeDirective = File.Exists(skillOptimizeDirectivePath)
                ? File.ReadAllText(skillOptimizeDirectivePath)
                : BuiltInSkillOptimizeDirective;

            if (!File.Exists(skillOptimizeDirectivePath))
                _logger.LogWarning("DreamService: skill optimize directive not found at {Path}; using built-in fallback", skillOptimizeDirectivePath);
            else
                _logger.LogInformation("DreamService: loaded skill optimize directive from {Path}", skillOptimizeDirectivePath);
        }

        if (_conversationLog is not null)
        {
            var prefDirectivePath = ResolvePath(_options.PreferenceDirectivePath, _profileOptions.BasePath);
            _prefDreamDirective = File.Exists(prefDirectivePath)
                ? File.ReadAllText(prefDirectivePath)
                : BuiltInPrefDirective;

            if (!File.Exists(prefDirectivePath))
                _logger.LogWarning("DreamService: pref directive not found at {Path}; using built-in fallback", prefDirectivePath);
            else
                _logger.LogInformation("DreamService: loaded pref directive from {Path}", prefDirectivePath);
        }

        if (_skillStore is not null && _conversationLog is not null)
        {
            var skillGapDirectivePath = ResolvePath(_options.SkillGapDirectivePath, _profileOptions.BasePath);
            _skillGapDirective = File.Exists(skillGapDirectivePath)
                ? File.ReadAllText(skillGapDirectivePath)
                : BuiltInSkillGapDirective;

            if (!File.Exists(skillGapDirectivePath))
                _logger.LogWarning("DreamService: skill gap directive not found at {Path}; using built-in fallback", skillGapDirectivePath);
            else
                _logger.LogInformation("DreamService: loaded skill gap directive from {Path}", skillGapDirectivePath);
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
            while (_userActivityMonitor.IsUserActive(TimeSpan.FromSeconds(30)))
            {
                _logger.LogDebug("DreamService: user recently active, delaying dream cycle by 5s");
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

            // Append recent feedback signals so the dream LLM has quality context
            if (_feedbackStore is not null)
            {
                var recentFeedback = await _feedbackStore.QueryRecentAsync(
                    since: DateTimeOffset.UtcNow.AddDays(-7),
                    maxResults: 50);

                if (recentFeedback.Count > 0)
                {
                    userMessage.AppendLine();
                    userMessage.AppendLine("Recent feedback signals (last 7 days):");
                    foreach (var fb in recentFeedback)
                    {
                        var detail = string.IsNullOrWhiteSpace(fb.Detail) ? string.Empty : $" (\"{fb.Detail}\")";
                        userMessage.AppendLine($"- [{fb.SignalType}] session {fb.SessionId}: {fb.Summary}{detail}");
                    }
                    userMessage.AppendLine();
                    _logger.LogDebug("DreamService: injected {Count} feedback signal(s) into dream prompt", recentFeedback.Count);
                }
            }

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
                await RunSkillGapDetectionPassAsync();

            if (_skillStore is not null)
                await ConsolidateSkillsAsync();

            await RunPreferenceInferencePassAsync();

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

        // Load recent usage events and build annotation maps
        var usageEvents = _skillUsageStore is not null
            ? await _skillUsageStore.QueryRecentAsync(DateTimeOffset.UtcNow.AddDays(-30), maxResults: 10000)
            : (IReadOnlyList<SkillInvocationEvent>)Array.Empty<SkillInvocationEvent>();

        var usageCount = usageEvents
            .GroupBy(e => e.SkillName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Build co-occurrence map: for each session, which skills were invoked together
        var skillsBySession = usageEvents
            .GroupBy(e => e.SessionId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.SkillName).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        var coOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, skills) in skillsBySession)
        {
            var sortedSkills = skills.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < sortedSkills.Count; i++)
                for (var j = i + 1; j < sortedSkills.Count; j++)
                {
                    var pair = $"{sortedSkills[i]}|{sortedSkills[j]}";
                    coOccurrences.TryGetValue(pair, out var cnt);
                    coOccurrences[pair] = cnt + 1;
                }
        }

        // Build per-skill co-occurrence list (skills that appear together more than once)
        var coUsed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pair, cnt) in coOccurrences.OrderByDescending(p => p.Value))
        {
            var parts = pair.Split('|');
            if (!coUsed.ContainsKey(parts[0])) coUsed[parts[0]] = [];
            if (!coUsed.ContainsKey(parts[1])) coUsed[parts[1]] = [];
            coUsed[parts[0]].Add(parts[1]);
            coUsed[parts[1]].Add(parts[0]);
        }

        var userMessage = new StringBuilder();
        userMessage.AppendLine($"The agent currently has {all.Count} skills. Consolidate them:");
        userMessage.AppendLine();

        var sparseThreshold = DateTimeOffset.UtcNow.AddDays(-7);
        for (var i = 0; i < all.Count; i++)
        {
            var s = all[i];
            var count = usageCount.TryGetValue(s.Name, out var c) ? c : 0;
            var usageAnnotation = $" [usage: {count}x in last 30d]";
            var coUsedAnnotation = coUsed.TryGetValue(s.Name, out var coSkills) && coSkills.Count > 0
                ? $" [co-used with: {string.Join(", ", coSkills.Take(3))}]"
                : string.Empty;
            var isSparse = s.Content.Length < 200 && s.CreatedAt < sparseThreshold;
            var sparseAnnotation = isSparse ? " [sparse-content: may need examples or steps]" : string.Empty;
            userMessage.AppendLine($"{i + 1}. [NAME:{s.Name}]{usageAnnotation}{coUsedAnnotation}{sparseAnnotation} summary: {s.Summary}");
            userMessage.AppendLine(s.Content);
            userMessage.AppendLine();
        }

        // Append co-occurrence section for the top pairs
        var topPairs = coOccurrences.OrderByDescending(p => p.Value).Take(10).ToList();
        if (topPairs.Count > 0)
        {
            userMessage.AppendLine();
            userMessage.AppendLine("Frequently co-used skill pairs (across sessions in last 30 days):");
            foreach (var (pair, cnt) in topPairs)
            {
                var parts = pair.Split('|');
                userMessage.AppendLine($"- {parts[0]} + {parts[1]}: {cnt} session(s)");
            }
        }

        // Append prefix cluster section for abstract parent guide detection
        var prefixClusters = all
            .Where(s => s.Name.Contains('/'))
            .GroupBy(s => s.Name[..s.Name.IndexOf('/')])
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (prefixClusters.Count > 0)
        {
            userMessage.AppendLine();
            userMessage.AppendLine("Skill name-prefix clusters (consider whether each cluster warrants an abstract parent guide skill):");
            foreach (var cluster in prefixClusters)
            {
                var names = cluster.OrderBy(s => s.Name).Select(s => s.Name).ToList();
                userMessage.AppendLine($"- '{cluster.Key}/*': {string.Join(", ", names)}");
            }
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

        // Safety guard: refuse to delete skills when nothing is being saved in return.
        // The directive says "never delete without replacement" — enforce it in code so an
        // LLM that violates the rule cannot silently destroy the skill library.
        if (allToDelete.Count > 0 && (result.ToSave is null || result.ToSave.Count == 0))
        {
            _logger.LogWarning(
                "DreamService: skill consolidation LLM proposed deleting {Count} skill(s) with no replacements — refusing to execute (possible LLM directive violation)",
                allToDelete.Count);
            return;
        }

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

            var seeAlso = dto.SeeAlso?
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .ToList();

            var skill = new Skill(
                Name: dto.Name.Trim(),
                Summary: dto.Summary?.Trim() ?? string.Empty,
                Content: dto.Content.Trim(),
                CreatedAt: minCreatedAt,
                UpdatedAt: DateTimeOffset.UtcNow,
                LastUsedAt: maxLastUsedAt,
                SeeAlso: seeAlso is { Count: > 0 } ? seeAlso : null);

            await _skillStore!.SaveAsync(skill);
            saved++;
            _logger.LogDebug("DreamService: saved merged skill '{Name}' (seeAlso: {SeeAlso})",
                skill.Name, skill.SeeAlso is { Count: > 0 } ? string.Join(", ", skill.SeeAlso) : "none");
        }

        await OptimizeSkillsAsync();

        _logger.LogInformation(
            "DreamService: skill consolidation complete — {Deleted} deleted, {Saved} saved",
            deleted, saved);
    }

    /// <summary>
    /// Identifies skills associated with poor-quality sessions and asks the LLM to improve them.
    /// Skipped when the skill usage store or feedback store is unavailable, or no at-risk skills are found.
    /// </summary>
    private async Task OptimizeSkillsAsync()
    {
        if (_skillUsageStore is null || _feedbackStore is null || _skillOptimizeDirective is null)
            return;

        var since = DateTimeOffset.UtcNow.AddDays(-30);

        var usageEvents = await _skillUsageStore.QueryRecentAsync(since, maxResults: 10000);
        if (usageEvents.Count == 0)
        {
            _logger.LogDebug("DreamService: no skill usage events in last 30 days; skipping optimization pass");
            return;
        }

        var recentFeedback = await _feedbackStore.QueryRecentAsync(since, maxResults: 1000);

        // Sessions that invoked at least one skill
        var sessionsWithSkills = usageEvents
            .Select(e => e.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Mark sessions as at-risk if they have Correction signals or poor/fair SessionSummary
        var atRiskSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var feedbackBySession = new Dictionary<string, List<FeedbackEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var fb in recentFeedback)
        {
            if (!sessionsWithSkills.Contains(fb.SessionId)) continue;

            if (!feedbackBySession.TryGetValue(fb.SessionId, out var list))
            {
                list = [];
                feedbackBySession[fb.SessionId] = list;
            }
            list.Add(fb);

            if (fb.SignalType is FeedbackSignalType.Correction or FeedbackSignalType.UserThumbsDown)
                atRiskSessions.Add(fb.SessionId);
            else if (fb.SignalType == FeedbackSignalType.SessionSummary)
            {
                var text = (fb.Summary + " " + fb.Detail).ToLowerInvariant();
                if (text.Contains("poor") || text.Contains("fair"))
                    atRiskSessions.Add(fb.SessionId);
            }
        }

        if (atRiskSessions.Count == 0)
        {
            _logger.LogDebug("DreamService: no at-risk sessions found; skipping optimization pass");
            return;
        }

        // Collect at-risk skill names from those sessions
        var atRiskSkillNames = usageEvents
            .Where(e => atRiskSessions.Contains(e.SessionId))
            .Select(e => e.SkillName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Load full content of at-risk skills
        var atRiskSkills = new List<Skill>();
        foreach (var name in atRiskSkillNames)
        {
            var skill = await _skillStore!.GetAsync(name);
            if (skill is not null)
                atRiskSkills.Add(skill);
        }

        // Also include structurally sparse skills for proactive review, even without failure signals.
        // A sparse skill (very short content, not brand-new) may have been recalled many times
        // but never actually improved — the agent should add examples or steps.
        var sparseCutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var sparseSkills = (await _skillStore!.ListAsync())
            .Where(s => s.Content.Length < 200 && s.CreatedAt < sparseCutoff
                        && !atRiskSkillNames.Contains(s.Name))
            .ToList();

        if (atRiskSkills.Count == 0 && sparseSkills.Count == 0)
        {
            _logger.LogDebug("DreamService: no at-risk or sparse skills found; skipping optimization pass");
            return;
        }

        _logger.LogInformation(
            "DreamService: optimization pass — {SkillCount} at-risk skill(s) from {SessionCount} at-risk session(s), {SparseCount} sparse skill(s)",
            atRiskSkills.Count, atRiskSessions.Count, sparseSkills.Count);

        // Build the optimization prompt
        var userMessage = new StringBuilder();
        userMessage.AppendLine($"The following skill(s) need review.");
        userMessage.AppendLine("At-risk skills were used in sessions with quality problems; improve them based on failure context.");
        userMessage.AppendLine("Sparse skills have minimal content and should be expanded with concrete examples or steps.");
        userMessage.AppendLine();

        foreach (var skill in atRiskSkills)
        {
            userMessage.AppendLine($"## Skill: {skill.Name}");
            userMessage.AppendLine(skill.Content);
            userMessage.AppendLine();

            // Gather feedback from sessions that used this skill and were at-risk
            var sessionsUsingSkill = usageEvents
                .Where(e => e.SkillName.Equals(skill.Name, StringComparison.OrdinalIgnoreCase) && atRiskSessions.Contains(e.SessionId))
                .Select(e => e.SessionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var relevantFeedback = sessionsUsingSkill
                .Where(feedbackBySession.ContainsKey)
                .SelectMany(s => feedbackBySession[s])
                .ToList();

            if (relevantFeedback.Count > 0)
            {
                userMessage.AppendLine("### Associated failure context:");
                foreach (var fb in relevantFeedback)
                {
                    var detail = string.IsNullOrWhiteSpace(fb.Detail) ? string.Empty : $" — \"{fb.Detail}\"";
                    userMessage.AppendLine($"- [{fb.SignalType}] {fb.Summary}{detail}");
                }
                userMessage.AppendLine();
            }
        }

        // Add sparse skills with a structural review context (no failure context, just expansion needed)
        if (sparseSkills.Count > 0)
        {
            userMessage.AppendLine("## Sparse skills (need expansion — add examples, steps, or clarifying detail):");
            userMessage.AppendLine();
            foreach (var skill in sparseSkills)
            {
                userMessage.AppendLine($"## Skill: {skill.Name} [SPARSE]");
                userMessage.AppendLine(skill.Content);
                userMessage.AppendLine();
                userMessage.AppendLine("### Review note: This skill has minimal content. Expand it with concrete steps, examples, and edge cases.");
                userMessage.AppendLine();
            }
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _skillOptimizeDirective),
            new(ChatRole.User, userMessage.ToString())
        };

        var response = await _llmClient.GetResponseAsync(messages, new ChatOptions());
        var raw = response.Text?.Trim() ?? string.Empty;
        var json = ExtractJsonObject(raw);

        if (string.IsNullOrEmpty(json))
        {
            _logger.LogWarning("DreamService: skill optimize LLM returned no parseable JSON; skipping optimization");
            return;
        }

        var result = JsonSerializer.Deserialize<SkillDreamResultDto>(json, JsonOptions);
        if (result is null)
        {
            _logger.LogWarning("DreamService: failed to deserialize skill optimize result; skipping");
            return;
        }

        var deleted = 0;
        var saved = 0;

        var allToDelete = new HashSet<string>(result.ToDelete ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var dto in result.ToSave ?? [])
            foreach (var srcName in dto.SourceNames ?? [])
                allToDelete.Add(srcName);

        foreach (var name in allToDelete)
        {
            await _skillStore!.DeleteAsync(name);
            deleted++;
            _logger.LogDebug("DreamService: optimization deleted skill '{Name}'", name);
        }

        var createdAtByName = (await _skillStore!.ListAsync())
            .ToDictionary(s => s.Name, s => s.CreatedAt, StringComparer.OrdinalIgnoreCase);

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

            var skill = new Skill(
                Name: dto.Name.Trim(),
                Summary: dto.Summary?.Trim() ?? string.Empty,
                Content: dto.Content.Trim(),
                CreatedAt: minCreatedAt,
                UpdatedAt: DateTimeOffset.UtcNow,
                LastUsedAt: null);

            await _skillStore!.SaveAsync(skill);
            saved++;
            _logger.LogDebug("DreamService: optimization saved improved skill '{Name}'", skill.Name);
        }

        _logger.LogInformation(
            "DreamService: skill optimization complete — {Deleted} deleted, {Saved} saved",
            deleted, saved);
    }

    /// <summary>
    /// Scans the conversation log for recurring request patterns that would benefit
    /// from a reusable skill and saves any discovered skills directly to the skill store.
    /// Runs before skill consolidation so that the consolidation pass can deduplicate
    /// any new skills alongside existing ones.
    /// </summary>
    private async Task RunSkillGapDetectionPassAsync()
    {
        if (_conversationLog is null || _skillStore is null || !_options.SkillGapEnabled)
            return;

        var entries = await _conversationLog.ReadAllAsync();
        if (entries.Count == 0)
        {
            _logger.LogDebug("DreamService: skill gap detection — no log entries; skipping");
            return;
        }

        var existingSkills = await _skillStore.ListAsync();

        _logger.LogInformation(
            "DreamService: skill gap detection pass — {EntryCount} log entries, {SkillCount} existing skills",
            entries.Count, existingSkills.Count);

        // Build user message: turns grouped by session + existing skill catalog
        var userMessage = new StringBuilder();
        userMessage.AppendLine("Review the following conversation log for recurring request patterns:");
        userMessage.AppendLine();

        var bySession = entries
            .GroupBy(e => e.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (sessionId, sessionEntries) in bySession)
        {
            userMessage.AppendLine($"## Session: {sessionId}");
            foreach (var e in sessionEntries)
                userMessage.AppendLine($"[{e.Role}] {e.Content}");
            userMessage.AppendLine();
        }

        if (existingSkills.Count > 0)
        {
            userMessage.AppendLine("## Existing skills (do not duplicate these):");
            foreach (var s in existingSkills)
                userMessage.AppendLine($"- {s.Name}: {s.Summary}");
            userMessage.AppendLine();
        }

        // Compute recurring term frequency across sessions as a stronger proactive signal.
        // Extract the first user message per session as the intent proxy, tokenize, and count
        // terms that appear in 2 or more sessions.
        var sessionFirstMessages = bySession
            .Select(kvp => kvp.Value.FirstOrDefault(e => e.Role == "user")?.Content ?? string.Empty)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        if (sessionFirstMessages.Count >= 2)
        {
            var termFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var msg in sessionFirstMessages)
            {
                var tokens = msg.ToLowerInvariant()
                    .Split([' ', '\n', '\t', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']'],
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length >= 4)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var token in tokens)
                {
                    termFreq.TryGetValue(token, out var cnt);
                    termFreq[token] = cnt + 1;
                }
            }

            var recurringTerms = termFreq
                .Where(kvp => kvp.Value >= 2)
                .OrderByDescending(kvp => kvp.Value)
                .Take(15)
                .ToList();

            if (recurringTerms.Count > 0)
            {
                userMessage.AppendLine("## Recurring topics across sessions (term frequency ≥ 2 sessions):");
                userMessage.AppendLine("Use these as stronger signals — high-frequency terms indicate recurring user needs.");
                foreach (var (term, count) in recurringTerms)
                    userMessage.AppendLine($"- \"{term}\": {count} session(s)");
                userMessage.AppendLine();
                _logger.LogDebug(
                    "DreamService: skill gap — {Count} recurring term(s) injected as pattern-frequency signal",
                    recurringTerms.Count);
            }
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _skillGapDirective ?? BuiltInSkillGapDirective),
            new(ChatRole.User, userMessage.ToString())
        };

        var response = await _llmClient.GetResponseAsync(messages, new ChatOptions());
        var raw = response.Text?.Trim() ?? string.Empty;
        var json = ExtractJsonObject(raw);

        if (string.IsNullOrEmpty(json))
        {
            _logger.LogWarning("DreamService: skill gap LLM returned no parseable JSON; skipping");
            return;
        }

        _logger.LogDebug("DreamService: skill gap JSON ({Length} chars): {Json}", json.Length, json);

        var result = JsonSerializer.Deserialize<SkillGapResultDto>(json, JsonOptions);
        var saved = 0;

        foreach (var dto in result?.ToSave ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Content))
                continue;

            var skill = new Skill(
                Name: dto.Name.Trim(),
                Summary: dto.Summary?.Trim() ?? string.Empty,
                Content: dto.Content.Trim(),
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                LastUsedAt: null);

            await _skillStore.SaveAsync(skill);
            saved++;
            _logger.LogDebug("DreamService: skill gap created new skill '{Name}'", skill.Name);
        }

        _logger.LogInformation("DreamService: skill gap detection complete — {Saved} new skill(s) created", saved);
    }

    /// <summary>
    /// Analyzes the accumulated conversation log for durable user preference patterns
    /// and saves inferred preferences as tagged memory entries.
    /// Always clears the log after the pass to prevent unbounded growth.
    /// </summary>
    private async Task RunPreferenceInferencePassAsync()
    {
        if (_conversationLog is null || !_options.PreferenceInferenceEnabled)
            return;

        var entries = await _conversationLog.ReadAllAsync();
        if (entries.Count == 0)
            return;

        _logger.LogInformation("DreamService: preference inference pass — {Count} log entries to analyze", entries.Count);

        try
        {
            // Build user message: turns grouped by session
            var userMessage = new StringBuilder();
            userMessage.AppendLine("Review the following conversation log for durable user preference patterns:");
            userMessage.AppendLine();

            var bySession = entries
                .GroupBy(e => e.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (sessionId, sessionEntries) in bySession)
            {
                userMessage.AppendLine($"## Session: {sessionId}");
                foreach (var e in sessionEntries)
                    userMessage.AppendLine($"[{e.Role}] {e.Content}");
                userMessage.AppendLine();
            }

            // Append recent feedback signals as additional quality context
            if (_feedbackStore is not null)
            {
                var recentFeedback = await _feedbackStore.QueryRecentAsync(
                    since: DateTimeOffset.UtcNow.AddDays(-7),
                    maxResults: 50);

                if (recentFeedback.Count > 0)
                {
                    userMessage.AppendLine("Recent feedback signals (last 7 days):");
                    foreach (var fb in recentFeedback)
                    {
                        var detail = string.IsNullOrWhiteSpace(fb.Detail) ? string.Empty : $" (\"{fb.Detail}\")";
                        userMessage.AppendLine($"- [{fb.SignalType}] session {fb.SessionId}: {fb.Summary}{detail}");
                    }
                    userMessage.AppendLine();
                    _logger.LogDebug("DreamService: injected {Count} feedback signal(s) into pref inference prompt", recentFeedback.Count);
                }
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _prefDreamDirective ?? BuiltInPrefDirective),
                new(ChatRole.User, userMessage.ToString())
            };

            var response = await _llmClient.GetResponseAsync(messages, new ChatOptions());
            var raw = response.Text?.Trim() ?? string.Empty;
            var json = ExtractJsonObject(raw);

            if (string.IsNullOrEmpty(json))
            {
                _logger.LogWarning("DreamService: preference inference LLM returned no parseable JSON; skipping");
            }
            else
            {
                _logger.LogDebug("DreamService: pref inference JSON ({Length} chars): {Json}", json.Length, json);

                var result = JsonSerializer.Deserialize<PrefDreamResultDto>(json, JsonOptions);
                var saved = 0;

                foreach (var dto in result?.ToSave ?? [])
                {
                    if (string.IsNullOrWhiteSpace(dto.Content))
                        continue;

                    // Ensure "inferred" tag is present
                    var tags = new List<string>(dto.Tags ?? []);
                    if (!tags.Contains("inferred", StringComparer.OrdinalIgnoreCase))
                        tags.Insert(0, "inferred");

                    // Merge metadata, ensuring source=inferred
                    var metadata = new Dictionary<string, string>(
                        dto.Metadata ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = "inferred"
                    };

                    var entry = new MemoryEntry(
                        Id: Guid.NewGuid().ToString("N")[..12],
                        Content: dto.Content.Trim(),
                        Category: string.IsNullOrWhiteSpace(dto.Category) ? "user-preferences/inferred" : dto.Category.Trim(),
                        Tags: tags,
                        CreatedAt: DateTimeOffset.UtcNow,
                        UpdatedAt: DateTimeOffset.UtcNow,
                        Metadata: metadata);

                    await _memory.SaveAsync(entry);
                    saved++;
                    _logger.LogDebug("DreamService: saved inferred preference {Id}: {Content}", entry.Id, entry.Content);
                }

                _logger.LogInformation("DreamService: preference inference pass complete — {Saved} preference(s) inferred", saved);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DreamService: preference inference pass failed");
        }
        finally
        {
            // Always clear the log regardless of LLM success/failure to prevent unbounded growth
            await _conversationLog.ClearAsync();
            _logger.LogDebug("DreamService: conversation log cleared after preference inference pass");
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

        Additionally, review any Correction feedback signals for anti-patterns — approaches the agent
        took that produced wrong or unhelpful results. Write these as new memory entries with:
        - category: "anti-patterns/{domain}" (e.g. "anti-patterns/file-operations", "anti-patterns/email")
        - content: "Don't [do X] for [reason Y] — instead [do Z]"
        - tags: ["anti-pattern"]
        Anti-pattern entries must be specific and actionable. Only write one if a clear failure pattern
        is evident from the Correction feedback. Do not speculate.
        """;

    private const string BuiltInSkillDirective = """
        You are a skill consolidation assistant. Review all skill documents for semantic overlap or near-duplication.
        Merge overlapping skills into improved combined ones.

        For skills sharing a name prefix (e.g. mcp/email, mcp/calendar, mcp/weather) shown in the
        prefix-cluster section: consider creating an abstract parent guide skill (e.g. mcp/guide) as
        a "when to use which" dispatch reference. The parent skill should be conceptual — a decision
        tree or selection guide — not a step-by-step procedure. Leaf skills remain procedural.
        Only create a parent guide if the cluster has 2 or more members and no adequate guide exists.

        For any skill, populate seeAlso with names of related skills the agent should consider alongside it:
        - Sibling skills in the same prefix cluster
        - Skills frequently co-used in the same session (shown in co-occurrence section)
        - Logical complements or prerequisites

        Return structured JSON:
        { "toDelete": [...names to remove...], "toSave": [...new/merged skills...] }
        Each skill in toSave: { "name", "summary", "content", "sourceNames", "seeAlso" }
        - seeAlso: optional list of related skill names (omit or use [] if none)
        The summary must be one sentence of 15 words or fewer.
        Every name in any sourceNames list must also appear in toDelete.
        If nothing needs consolidation, return: { "toDelete": [], "toSave": [] }
        """;

    private const string BuiltInSkillOptimizeDirective = """
        You are a skill improvement assistant. Review each skill and its associated failure context.
        Identify what step or gap likely caused the failure and produce an improved skill that addresses it.
        Return structured JSON: { "toDelete": [...names to remove...], "toSave": [...improved skills...] }
        Each skill in toSave: { "name", "summary", "content", "sourceNames" }
        List the original skill name in sourceNames to trigger replacement.
        The summary must be one sentence of 15 words or fewer.
        Only improve skills where the failure is clearly addressable by better instructions.
        If no improvements are warranted, return: { "toDelete": [], "toSave": [] }
        """;

    private const string BuiltInSkillGapDirective = """
        You are a skill gap detection assistant. Review the conversation log for recurring request
        patterns that would benefit from a reusable skill.

        Only suggest a new skill when the same type of request appears 2 or more times across
        different sessions, or with clear recurring intent in a single session.

        Existing skills are listed below — do not suggest skills already adequately covered by them.

        Return ONLY a JSON object:
        { "toSave": [ { "name": "...", "summary": "...", "content": "..." } ] }

        Rules:
        - name: short, lowercase, hyphen-separated (e.g. "summarize-emails", "daily-standup")
        - summary: one sentence, 15 words or fewer
        - content: step-by-step instructions the agent should follow when executing this skill
        - Only suggest skills with clear, repeatable value across sessions

        If no recurring patterns warrant a new skill, return: { "toSave": [] }
        """;

    private const string BuiltInPrefDirective = """
        You are a user preference inference assistant. Review the conversation log for durable, recurring preference patterns.
        Look for: formatting preferences, comment style, tool corrections, topic clusters, and communication style signals.

        Apply these sentiment-based thresholds before writing a preference:
        - Very irritated (repeated strong correction, visible frustration): 1 occurrence is enough
        - Mildly frustrated (mild correction, gentle pushback): 2 occurrences needed
        - Minor/casual suggestion: 3 or more occurrences needed

        For preferences touching security keys, passwords, financial decisions, or sending sensitive information:
        add "requires_user_permission": "true" to metadata and note in content that user confirmation is required before acting.

        Return ONLY a JSON object in this exact format:
        { "toSave": [ { "content": "...", "category": "user-preferences/inferred", "tags": ["inferred"], "metadata": { "source": "inferred" } } ] }

        If no durable patterns are evident, return: { "toSave": [] }
        Each entry needs: content (what was learned), category (defaults to "user-preferences/inferred"),
        tags (must include "inferred"), metadata (must include "source": "inferred").
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
        IReadOnlyList<string>? SourceNames,
        IReadOnlyList<string>? SeeAlso);

    private sealed record PrefDreamResultDto(List<PrefEntryDto>? ToSave);

    private sealed record PrefEntryDto(
        string Content,
        string? Category,
        IReadOnlyList<string>? Tags,
        IReadOnlyDictionary<string, string>? Metadata);

    private sealed record SkillGapResultDto(List<SkillGapEntryDto>? ToSave);

    private sealed record SkillGapEntryDto(string Name, string? Summary, string Content);
}
