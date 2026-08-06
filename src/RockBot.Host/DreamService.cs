using System.Text;
using System.Text.Json;
using Cronos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Messaging;
using RockBot.Tools;

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
    private readonly TierRoutingLogger? _tierRoutingLogger;
    private readonly IDlqSampler? _dlqSampler;
    private readonly IToolCallLog? _toolCallLog;
    private readonly IWispExecutionLog? _wispExecutionLog;
    private readonly IKnowledgeGraph? _knowledgeGraph;
    private readonly IWorkingMemory? _workingMemory;
    private readonly ILlmClient _llmClient;
    private readonly TieredChatClientRegistry? _tieredRegistry;
    private readonly IAgentWorkSerializer _workSerializer;
    private readonly IUserActivityMonitor _userActivityMonitor;
    private readonly AgentClock _clock;
    private readonly DreamOptions _options;
    private readonly AgentProfileOptions _profileOptions;
    private readonly ILogger<DreamService> _logger;
    private readonly RockBot.Observation.IObservationPipelineCoordinator? _observationCoordinator;
    private readonly ConversationLogTranscriptAdapter? _observationTranscriptAdapter;
    private readonly IMemoryContradictionDetector? _contradictionDetector;
    private readonly IFailureClusterStore? _failureClusterStore;
    private readonly ISkillResourceUsageStore? _skillResourceUsageStore;
    private readonly IRepairTicketStore? _repairTicketStore;
    private readonly IReadOnlyDictionary<RepairTarget, IRepairTargetApplier>? _repairAppliers;
    private readonly IRepairTicketVerifier? _repairTicketVerifier;
    private readonly RepairTicketOptions? _repairOptions;
    private readonly IReadOnlyList<IToolSkillProvider> _toolSkillProviders;
    private readonly IReadOnlyList<IPrunableLog> _prunableLogs;
    private Timer? _timer;
    private CronExpression? _cron;
    private string? _dreamDirective;
    private string? _skillDreamDirective;
    private string? _skillOptimizeDirective;
    private string? _prefDreamDirective;
    private string? _skillGapDirective;
    private string? _memoryMiningDirective;
    private string? _episodeDirective;
    private string? _tierRoutingDirective;
    private string? _sequenceSkillDirective;
    private string? _entityExtractionDirective;
    private string? _graphConsolidationDirective;
    private string? _dlqDirective;
    private string? _identityDirective;
    private string? _wispFailureDirective;
    private string? _wispSuccessDirective;
    private string? _toolSuccessLearningDirective;
    private string? _contradictionSweepDirective;
    private string? _repairTicketCreationDirective;
    private IReadOnlyList<LlmPricingRow>? _pricingRows;

    public DreamService(
        ILongTermMemory memory,
        IEnumerable<ISkillStore> skillStores,
        ILlmClient llmClient,
        IAgentWorkSerializer workSerializer,
        IUserActivityMonitor userActivityMonitor,
        AgentClock clock,
        IOptions<DreamOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<DreamService> logger,
        IFeedbackStore? feedbackStore = null,
        ISkillUsageStore? skillUsageStore = null,
        IConversationLog? conversationLog = null,
        TierRoutingLogger? tierRoutingLogger = null,
        IDlqSampler? dlqSampler = null,
        IToolCallLog? toolCallLog = null,
        IKnowledgeGraph? knowledgeGraph = null,
        IWispExecutionLog? wispExecutionLog = null,
        IWorkingMemory? workingMemory = null,
        RockBot.Observation.IObservationPipelineCoordinator? observationCoordinator = null,
        ConversationLogTranscriptAdapter? observationTranscriptAdapter = null,
        IMemoryContradictionDetector? contradictionDetector = null,
        IFailureClusterStore? failureClusterStore = null,
        ISkillResourceUsageStore? skillResourceUsageStore = null,
        IRepairTicketStore? repairTicketStore = null,
        IEnumerable<IRepairTargetApplier>? repairAppliers = null,
        IRepairTicketVerifier? repairTicketVerifier = null,
        IOptions<RepairTicketOptions>? repairOptions = null,
        IEnumerable<IToolSkillProvider>? toolSkillProviders = null,
        TieredChatClientRegistry? tieredRegistry = null,
        IEnumerable<IPrunableLog>? prunableLogs = null)
    {
        _memory = memory;
        _skillStore = skillStores.FirstOrDefault();
        _feedbackStore = feedbackStore;
        _skillUsageStore = skillUsageStore;
        _conversationLog = conversationLog;
        _tierRoutingLogger = tierRoutingLogger;
        _dlqSampler = dlqSampler;
        _toolCallLog = toolCallLog;
        _knowledgeGraph = knowledgeGraph;
        _wispExecutionLog = wispExecutionLog;
        _workingMemory = workingMemory;
        _llmClient = llmClient;
        _tieredRegistry = tieredRegistry;
        _workSerializer = workSerializer;
        _userActivityMonitor = userActivityMonitor;
        _clock = clock;
        _options = options.Value;
        _profileOptions = profileOptions.Value;
        _logger = logger;
        _observationCoordinator = observationCoordinator;
        _observationTranscriptAdapter = observationTranscriptAdapter;
        _contradictionDetector = contradictionDetector;
        _failureClusterStore = failureClusterStore;
        _skillResourceUsageStore = skillResourceUsageStore;
        _repairTicketStore = repairTicketStore;
        _repairTicketVerifier = repairTicketVerifier;
        _repairOptions = repairOptions?.Value;
        if (repairAppliers is not null)
        {
            var map = new Dictionary<RepairTarget, IRepairTargetApplier>();
            foreach (var applier in repairAppliers)
                map[applier.Target] = applier;
            _repairAppliers = map;
        }
        _toolSkillProviders = toolSkillProviders?.ToList() ?? (IReadOnlyList<IToolSkillProvider>)Array.Empty<IToolSkillProvider>();
        _prunableLogs = prunableLogs?.ToList() ?? (IReadOnlyList<IPrunableLog>)Array.Empty<IPrunableLog>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("DreamService: dreaming is disabled; skipping timer setup");
            return Task.CompletedTask;
        }

        LoadDirectives(initialLoad: true);

        try
        {
            _cron = CronExpression.Parse(_options.CronSchedule,
                _options.CronSchedule.Split(' ').Length == 6
                    ? CronFormat.IncludeSeconds
                    : CronFormat.Standard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DreamService: invalid cron expression '{Cron}'; dreaming disabled",
                _options.CronSchedule);
            return Task.CompletedTask;
        }

        _timer = new Timer(
            state => { _ = OnTimerTickAsync(); },
            null,
            _options.InitialDelay,
            Timeout.InfiniteTimeSpan);

        _logger.LogInformation(
            "DreamService: scheduled — first cycle in {InitialDelay}, then on cron '{Cron}'",
            _options.InitialDelay, _options.CronSchedule);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// Re-reads every dream-pass directive file from disk into the in-memory fields.
    /// Called once at StartAsync and again at the top of every dream cycle so live edits
    /// to /data/agent/*-dream.md, skill-optimize.md, etc. take effect without a pod
    /// restart. The cost is ~18 small file reads per cycle (negligible).
    /// </summary>
    /// <param name="initialLoad">
    /// True on first load from <see cref="StartAsync"/>; false on per-cycle reload.
    /// Only used to choose between an Information ("loading") and Debug ("reloading") log line
    /// — per-file load messages are always Debug so the per-cycle reload does not spam logs.
    /// </param>
    private void LoadDirectives(bool initialLoad)
    {
        // Once per cycle (every 12h) — keep at Information so the per-cycle reload
        // is visible in default logs without elevating the namespace to Debug.
        _logger.LogInformation(
            "DreamService: {Mode} dream directives from disk",
            initialLoad ? "loading" : "reloading");

        // Load shared memory rules (if present) to prepend to the dream directive
        var memoryRulesPath = ResolvePath("memory-rules.md", _profileOptions.BasePath);
        var memoryRules = File.Exists(memoryRulesPath) ? File.ReadAllText(memoryRulesPath) : string.Empty;
        if (!string.IsNullOrEmpty(memoryRules))
            _logger.LogDebug("DreamService: loaded memory-rules from {Path}", memoryRulesPath);

        var directivePath = ResolvePath(_options.DirectivePath, _profileOptions.BasePath);
        var dreamDirective = File.Exists(directivePath)
            ? File.ReadAllText(directivePath)
            : BuiltInDirective;
        if (!File.Exists(directivePath))
            _logger.LogWarning("DreamService: dream directive not found at {Path}; using built-in fallback", directivePath);
        else
            _logger.LogDebug("DreamService: loaded dream directive from {Path}", directivePath);

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
                _logger.LogDebug("DreamService: loaded skill directive from {Path}", skillDirectivePath);

            var skillOptimizeDirectivePath = ResolvePath(_options.SkillOptimizeDirectivePath, _profileOptions.BasePath);
            _skillOptimizeDirective = File.Exists(skillOptimizeDirectivePath)
                ? File.ReadAllText(skillOptimizeDirectivePath)
                : BuiltInSkillOptimizeDirective;
            if (!File.Exists(skillOptimizeDirectivePath))
                _logger.LogWarning("DreamService: skill optimize directive not found at {Path}; using built-in fallback", skillOptimizeDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded skill optimize directive from {Path}", skillOptimizeDirectivePath);
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
                _logger.LogDebug("DreamService: loaded pref directive from {Path}", prefDirectivePath);
        }

        if (_conversationLog is not null)
        {
            var memoryMiningDirectivePath = ResolvePath(_options.MemoryMiningDirectivePath, _profileOptions.BasePath);
            _memoryMiningDirective = File.Exists(memoryMiningDirectivePath)
                ? File.ReadAllText(memoryMiningDirectivePath)
                : null;
            if (!File.Exists(memoryMiningDirectivePath))
                _logger.LogDebug("DreamService: memory mining directive not found at {Path}; using built-in", memoryMiningDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded memory mining directive from {Path}", memoryMiningDirectivePath);

            var episodeDirectivePath = ResolvePath(_options.EpisodeDirectivePath, _profileOptions.BasePath);
            _episodeDirective = File.Exists(episodeDirectivePath)
                ? File.ReadAllText(episodeDirectivePath)
                : null;
            if (!File.Exists(episodeDirectivePath))
                _logger.LogDebug("DreamService: episode directive not found at {Path}; using built-in", episodeDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded episode directive from {Path}", episodeDirectivePath);
        }

        if (_knowledgeGraph is not null)
        {
            var entityDirectivePath = ResolvePath(_options.EntityExtractionDirectivePath, _profileOptions.BasePath);
            _entityExtractionDirective = File.Exists(entityDirectivePath)
                ? File.ReadAllText(entityDirectivePath)
                : null;
            if (!File.Exists(entityDirectivePath))
                _logger.LogDebug("DreamService: entity extraction directive not found at {Path}; using built-in", entityDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded entity extraction directive from {Path}", entityDirectivePath);

            var graphConsolidationPath = ResolvePath(_options.GraphConsolidationDirectivePath, _profileOptions.BasePath);
            _graphConsolidationDirective = File.Exists(graphConsolidationPath)
                ? File.ReadAllText(graphConsolidationPath)
                : null;
            if (!File.Exists(graphConsolidationPath))
                _logger.LogDebug("DreamService: graph consolidation directive not found at {Path}; using built-in", graphConsolidationPath);
            else
                _logger.LogDebug("DreamService: loaded graph consolidation directive from {Path}", graphConsolidationPath);
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
                _logger.LogDebug("DreamService: loaded skill gap directive from {Path}", skillGapDirectivePath);
        }

        if (_options.TierRoutingReviewEnabled)
        {
            var tierRoutingDirectivePath = ResolvePath(_options.TierRoutingDirectivePath, _profileOptions.BasePath);
            _tierRoutingDirective = File.Exists(tierRoutingDirectivePath)
                ? File.ReadAllText(tierRoutingDirectivePath)
                : null;
            if (!File.Exists(tierRoutingDirectivePath))
                _logger.LogDebug("DreamService: tier routing directive not found at {Path}; using built-in", tierRoutingDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded tier routing directive from {Path}", tierRoutingDirectivePath);

            // Load the pricing table so the routing analyzer can compute USD cost.
            // Optional — if the file is missing or malformed, the analyzer returns null
            // cost fields and the LLM proceeds without that signal. Reloaded on every
            // LoadDirectives call so operator edits to llm-pricing.json take effect at
            // the next dream cycle without restarting the agent.
            var pricingPath = ResolvePath("llm-pricing.json", _profileOptions.BasePath);
            if (File.Exists(pricingPath))
            {
                try
                {
                    var pricingJson = File.ReadAllText(pricingPath);
                    _pricingRows = JsonSerializer.Deserialize<List<LlmPricingRow>>(pricingJson, JsonOptions);
                    if (initialLoad)
                        _logger.LogInformation(
                            "DreamService: loaded {Count} pricing rows from {Path}",
                            _pricingRows?.Count ?? 0, pricingPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DreamService: failed to parse pricing file {Path}; cost analysis will be skipped", pricingPath);
                }
            }
        }

        if (_options.SequenceSkillDetectionEnabled && _toolCallLog is not null && _skillStore is not null)
        {
            var sequenceSkillDirectivePath = ResolvePath(_options.SequenceSkillDirectivePath, _profileOptions.BasePath);
            _sequenceSkillDirective = File.Exists(sequenceSkillDirectivePath)
                ? File.ReadAllText(sequenceSkillDirectivePath)
                : null;
            if (!File.Exists(sequenceSkillDirectivePath))
                _logger.LogDebug("DreamService: sequence skill directive not found at {Path}; using built-in", sequenceSkillDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded sequence skill directive from {Path}", sequenceSkillDirectivePath);
        }

        if (_options.DlqReviewEnabled && _dlqSampler is not null)
        {
            var dlqDirectivePath = ResolvePath(_options.DlqDirectivePath, _profileOptions.BasePath);
            _dlqDirective = File.Exists(dlqDirectivePath)
                ? File.ReadAllText(dlqDirectivePath)
                : null;
            if (!File.Exists(dlqDirectivePath))
                _logger.LogDebug("DreamService: DLQ directive not found at {Path}; using built-in", dlqDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded DLQ directive from {Path}", dlqDirectivePath);
        }

        if (_options.IdentityReflectionEnabled)
        {
            var identityDirectivePath = ResolvePath(_options.IdentityDirectivePath, _profileOptions.BasePath);
            _identityDirective = File.Exists(identityDirectivePath)
                ? File.ReadAllText(identityDirectivePath)
                : null;
            if (!File.Exists(identityDirectivePath))
                _logger.LogDebug("DreamService: identity directive not found at {Path}; using built-in", identityDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded identity directive from {Path}", identityDirectivePath);
        }

        if (_options.WispFailureAnalysisEnabled && _wispExecutionLog is not null && _skillStore is not null)
        {
            var wispDirectivePath = ResolvePath(_options.WispFailureDirectivePath, _profileOptions.BasePath);
            _wispFailureDirective = File.Exists(wispDirectivePath)
                ? File.ReadAllText(wispDirectivePath)
                : null;
            if (!File.Exists(wispDirectivePath))
                _logger.LogDebug("DreamService: wisp failure directive not found at {Path}; using built-in", wispDirectivePath);
            else
                _logger.LogDebug("DreamService: loaded wisp failure directive from {Path}", wispDirectivePath);
        }

        if (_options.WispSuccessAnalysisEnabled && _wispExecutionLog is not null && _skillStore is not null)
        {
            var wispSuccessPath = ResolvePath(_options.WispSuccessDirectivePath, _profileOptions.BasePath);
            _wispSuccessDirective = File.Exists(wispSuccessPath)
                ? File.ReadAllText(wispSuccessPath)
                : null;
            if (!File.Exists(wispSuccessPath))
                _logger.LogDebug("DreamService: wisp success directive not found at {Path}; using built-in", wispSuccessPath);
            else
                _logger.LogDebug("DreamService: loaded wisp success directive from {Path}", wispSuccessPath);
        }

        if (_options.ToolSuccessLearningEnabled && _toolCallLog is not null)
        {
            var path = ResolvePath(_options.ToolSuccessLearningDirectivePath, _profileOptions.BasePath);
            _toolSuccessLearningDirective = File.Exists(path)
                ? File.ReadAllText(path)
                : null;
            if (!File.Exists(path))
                _logger.LogDebug("DreamService: tool-success-learning directive not found at {Path}; using built-in", path);
            else
                _logger.LogDebug("DreamService: loaded tool-success-learning directive from {Path}", path);
        }

        if (_options.ContradictionSweepEnabled)
        {
            var path = ResolvePath(_options.ContradictionSweepDirectivePath, _profileOptions.BasePath);
            _contradictionSweepDirective = File.Exists(path)
                ? File.ReadAllText(path)
                : null;
            if (!File.Exists(path))
                _logger.LogDebug("DreamService: contradiction sweep directive not found at {Path}; using built-in", path);
            else
                _logger.LogDebug("DreamService: loaded contradiction sweep directive from {Path}", path);
        }

        if (RepairLoopEnabled)
        {
            var path = ResolvePath(_repairOptions!.CreationDirectivePath, _profileOptions.BasePath);
            _repairTicketCreationDirective = File.Exists(path)
                ? File.ReadAllText(path)
                : null;
            if (!File.Exists(path))
                _logger.LogDebug("DreamService: repair-ticket creation directive not found at {Path}; using built-in", path);
            else
                _logger.LogDebug("DreamService: loaded repair-ticket creation directive from {Path}", path);
        }
    }

    private async Task OnTimerTickAsync()
    {
        await DreamAsync();
        ArmNextCronTimer();
    }

    private void ArmNextCronTimer()
    {
        if (_cron is null) return;

        var next = _cron.GetNextOccurrence(_clock.Now, _clock.Zone);
        if (next is null)
        {
            _logger.LogWarning("DreamService: cron '{Cron}' has no future occurrences; dreaming stopped",
                _options.CronSchedule);
            return;
        }

        var delay = next.Value - _clock.Now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _timer?.Change(delay, Timeout.InfiniteTimeSpan);
        _logger.LogInformation("DreamService: next dream cycle at {Next} (in {Delay:g})", next.Value, delay);
    }

    private async Task DreamAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Acquire the work serializer slot so user messages can preempt the dream.
        // TryAcquireForScheduledAsync is non-blocking: if a user loop holds the slot, skip.
        var slot = await _workSerializer.TryAcquireForScheduledAsync(CancellationToken.None);
        if (slot is null)
        {
            _logger.LogInformation("DreamService: user loop active, skipping dream cycle");
            return;
        }

        // Re-read directives from disk so live edits to /data/agent/*-dream.md and
        // skill-optimize.md take effect without a pod restart. Cheap (~18 small file
        // reads) and runs once per cycle.
        LoadDirectives(initialLoad: false);

        _logger.LogInformation("DreamService: dream cycle starting");

        try
        {
            var ct = slot.Token;

            // Log retention runs first — append-only JSONL logs must be capped even on
            // cycles where there is nothing to consolidate.
            await RunLogRetentionPassAsync(ct);

            // Consolidation is one pass among many and must not gate the rest of the
            // cycle. It lives in its own method precisely so that its early exits (too
            // few entries to merge, or a failed LLM call) return from *it* rather than
            // aborting the cycle. Inlining it here previously deadlocked a fresh agent:
            // consolidation needs >=2 memory entries, memory mining is what creates
            // them, and mining runs after consolidation — so an empty store skipped the
            // cycle at this point and memory could never become non-empty.
            var (deleted, saved) = await RunMemoryConsolidationPassAsync(ct);

            if (_skillStore is not null)
            { ct.ThrowIfCancellationRequested(); await RunSkillGapDetectionPassAsync(ct); }

            if (_skillStore is not null)
            { ct.ThrowIfCancellationRequested(); await ConsolidateSkillsAsync(ct); }

            ct.ThrowIfCancellationRequested(); await RunEpisodeExtractionPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunEntityExtractionPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunGraphConsolidationPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunMemoryMiningPassAsync(ct);

            // Observation framework reads the same conversation log used by
            // memory mining and preference inference, so it must run before
            // RunPreferenceInferencePassAsync (which clears the log in its
            // finally block).
            ct.ThrowIfCancellationRequested(); await RunObservationPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunPreferenceInferencePassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunSequenceSkillDetectionPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunWispFailureAnalysisPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunWispSuccessAnalysisPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunProvisionalValidationPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunToolSuccessLearningPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunTierRoutingReviewPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunDlqReviewPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunIdentityReflectionPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunContradictionSweepPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunRepairTicketCreationPassAsync(ct);

            ct.ThrowIfCancellationRequested(); await RunRepairTicketApplyPassAsync(ct);

            sw.Stop();
            _logger.LogInformation(
                "DreamService: dream cycle complete — {Deleted} deleted, {Saved} saved, elapsed {Elapsed}",
                deleted, saved, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DreamService: dream cycle preempted by user request");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DreamService: dream cycle failed");
        }
        finally
        {
            await slot.DisposeAsync();
        }
    }

    /// <summary>
    /// Merges and prunes long-term memory entries via the "memory dream" pass.
    /// Returns the number of entries deleted and saved.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate method rather than inline in <c>DreamAsync</c>: both of
    /// its early exits — fewer than two entries to merge, and a failed LLM call — used
    /// to <c>return</c> straight out of the dream cycle, silently skipping every later
    /// pass including memory mining. That deadlocked a fresh agent, whose memory store
    /// only becomes non-empty once mining has run.
    /// </remarks>
    private async Task<(int Deleted, int Saved)> RunMemoryConsolidationPassAsync(CancellationToken ct)
    {
        if (!_options.MemoryConsolidationEnabled)
        {
            _logger.LogInformation("DreamService: memory consolidation disabled; skipping");
            return (0, 0);
        }

        var all = await _memory.SearchAsync(new MemorySearchCriteria(MaxResults: 1000));

        if (all.Count < 2)
        {
            _logger.LogInformation(
                "DreamService: only {Count} memory entries — nothing to consolidate; skipping consolidation",
                all.Count);
            return (0, 0);
        }

        _logger.LogDebug("DreamService: fetched {Count} memory entries for consolidation", all.Count);

        // Apply importance decay to unreferenced entries before consolidation
        await RunImportanceDecayPassAsync(all);

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
            var subjectTime = FormatSubjectTimeForPrompt(e.Metadata);
            userMessage.AppendLine(
                $"{i + 1}. [ID:{e.Id}] category={e.Category ?? "uncategorized"} " +
                $"importance={e.ImportanceScore:F2} reinforced={e.ReinforcementCount}× " +
                $"first={e.CreatedAt:yyyy-MM-dd} last={e.LastSeenAt:yyyy-MM-dd}" +
                $"{subjectTime} tags=[{tags}]");
            userMessage.AppendLine($"   {e.Content}");
        }

        var result = await InvokeDreamPassAsync<DreamResultDto>(
            "memory dream",
            _dreamDirective!,
            userMessage.ToString(),
            ct);
        if (result is null) return (0, 0);

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

        // Build source-entry lookup (case-insensitive on ID) for merge arithmetic
        var byId = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in all)
            byId[e.Id] = e;

        foreach (var dto in result.ToSave ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Content))
                continue;

            var sourceIds = dto.SourceIds ?? [];
            var sources = sourceIds
                .Where(id => byId.ContainsKey(id))
                .Select(id => byId[id])
                .ToList();

            // CreatedAt = earliest source (first-seen preserved); UpdatedAt = now (record rewritten)
            var firstSeen = sources.Count > 0 ? sources.Min(s => s.CreatedAt) : DateTimeOffset.UtcNow;

            // LastSeenAt = most recent source reinforcement; never "now" on dream housekeeping
            var lastSeen = sources.Count > 0 ? sources.Max(s => s.LastSeenAt) : DateTimeOffset.UtcNow;

            // ReinforcementCount = sum of source counts; singleton merge (1 source) preserves its count
            var reinforcementCount = sources.Count > 0 ? sources.Sum(s => s.ReinforcementCount) : 1;

            // LLM-provided importance wins; otherwise carry forward max from sources
            var importance = dto.Importance
                ?? (sources.Count > 0 ? sources.Max(s => s.ImportanceScore) : 0.5f);

            var mergedMetadata = MergeSubjectTimeMetadata(sources);

            var entry = new MemoryEntry(
                Id: Guid.NewGuid().ToString("N")[..12],
                Content: dto.Content.Trim(),
                Category: string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category.Trim(),
                Tags: dto.Tags ?? [],
                CreatedAt: firstSeen,
                UpdatedAt: DateTimeOffset.UtcNow,
                Metadata: mergedMetadata,
                ImportanceScore: Math.Clamp(importance, 0f, 1f))
            {
                LastSeenAt = lastSeen,
                ReinforcementCount = reinforcementCount
            };

            await _memory.SaveAsync(entry);
            saved++;
            _logger.LogDebug("DreamService: saved entry {Id} ({Category}, importance={Importance:F2}, reinforced={Count}×): {Content}",
                entry.Id, entry.Category ?? "(none)", entry.ImportanceScore, entry.ReinforcementCount, entry.Content);
        }

        return (deleted, saved);
    }

    private async Task ConsolidateSkillsAsync(CancellationToken ct)
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

        var singletonPrefixes = GetSingletonPrefixes(_toolSkillProviders);

        var userMessage = BuildSkillConsolidationUserMessage(
            all, usageCount, coUsed, coOccurrences, singletonPrefixes, DateTimeOffset.UtcNow);

        var result = await InvokeDreamPassAsync<SkillDreamResultDto>(
            "skill consolidation",
            _skillDreamDirective!,
            userMessage,
            ct);
        if (result is null) return;

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

        // Capture attached resources from sources BEFORE deletion. By default the
        // merge unions all attachments from the source skills onto the merged skill;
        // the LLM can narrow this via dto.Resources (an allowlist of filenames).
        // Without this capture step, merges into a new name silently destroy attached
        // wisps and scripts since FileSkillStore's manifest-preserve only fires when
        // the destination already exists on disk.
        var capturedResourcesByDto = new Dictionary<string, IReadOnlyList<SkillResourceInput>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in result.ToSave ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Name) || dto.SourceNames is null || dto.SourceNames.Count == 0)
                continue;
            var captured = await CaptureResourceInputsAsync(dto.SourceNames, dto.Resources, ct);
            if (captured.Count > 0)
                capturedResourcesByDto[dto.Name.Trim()] = captured;
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

            if (capturedResourcesByDto.TryGetValue(skill.Name, out var carriedResources))
            {
                await _skillStore!.SaveAsync(skill, carriedResources.ToList());
                _logger.LogDebug("DreamService: saved merged skill '{Name}' with {Count} carried resource(s) (seeAlso: {SeeAlso})",
                    skill.Name, carriedResources.Count,
                    skill.SeeAlso is { Count: > 0 } ? string.Join(", ", skill.SeeAlso) : "none");
            }
            else
            {
                await _skillStore!.SaveAsync(skill);
                _logger.LogDebug("DreamService: saved merged skill '{Name}' (seeAlso: {SeeAlso})",
                    skill.Name, skill.SeeAlso is { Count: > 0 } ? string.Join(", ", skill.SeeAlso) : "none");
            }
            saved++;
        }

        await OptimizeSkillsAsync(ct);

        _logger.LogInformation(
            "DreamService: skill consolidation complete — {Deleted} deleted, {Saved} saved",
            deleted, saved);
    }

    /// <summary>
    /// Identifies skills associated with poor-quality sessions and asks the LLM to improve them.
    /// Skipped when the skill usage store or feedback store is unavailable, or no at-risk skills are found.
    /// </summary>
    private async Task OptimizeSkillsAsync(CancellationToken ct)
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

        // Tool-retry signal: sessions that called the same tool repeatedly with different args
        // until one succeeded indicate the guiding skill left an ambiguity. The arg pair
        // (failed → succeeded) is exactly the context the optimizer needs to tighten the skill.
        var toolRetryNotesBySession =
            await DetectToolRetrySessionsAsync(sessionsWithSkills, since);
        foreach (var sessionId in toolRetryNotesBySession.Keys)
            atRiskSessions.Add(sessionId);

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
            var attachedAnnotation = FormatAttachedAnnotation(skill.Manifest);
            userMessage.AppendLine($"## Skill: {skill.Name}{attachedAnnotation}");
            const int OptimizeCap = 800;
            var displayContent = skill.Content.Length > OptimizeCap
                ? skill.Content[..OptimizeCap] + "\n[... truncated ...]"
                : skill.Content;
            userMessage.AppendLine(displayContent);
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

            // Tool-retry context: failed→succeeded arg pairs from sessions that used this skill.
            // These show exactly which ambiguity in the skill caused costly retries.
            var retryNotesForSkill = sessionsUsingSkill
                .Where(toolRetryNotesBySession.ContainsKey)
                .SelectMany(s => toolRetryNotesBySession[s])
                .ToList();

            if (retryNotesForSkill.Count > 0)
            {
                userMessage.AppendLine("### Tool retry-until-success patterns (skill ambiguity signals):");
                foreach (var note in retryNotesForSkill)
                    userMessage.AppendLine($"- {note}");
                userMessage.AppendLine();
                userMessage.AppendLine(
                    "Consider tightening the skill to specify the verified argument value(s) above, " +
                    "replacing any hedging language (\"typically X and sometimes Y\") with the concrete answer.");
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
                var attachedAnnotation = FormatAttachedAnnotation(skill.Manifest);
                userMessage.AppendLine($"## Skill: {skill.Name} [SPARSE]{attachedAnnotation}");
                userMessage.AppendLine(skill.Content);
                userMessage.AppendLine();
                userMessage.AppendLine("### Review note: This skill has minimal content. Expand it with concrete steps, examples, and edge cases.");
                userMessage.AppendLine();
            }
        }

        var result = await InvokeDreamPassAsync<SkillDreamResultDto>(
            "skill optimization",
            _skillOptimizeDirective,
            userMessage.ToString(),
            ct);
        if (result is null) return;

        var deleted = 0;
        var saved = 0;

        var allToDelete = new HashSet<string>(result.ToDelete ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var dto in result.ToSave ?? [])
            foreach (var srcName in dto.SourceNames ?? [])
                allToDelete.Add(srcName);

        // Capture attachments BEFORE deletion so the improved skill carries them
        // forward. Optimize is the surgical-prose path — it rewrites content for
        // the same name — but the source must be deleted first so the new save
        // takes the slot. Without this capture the rewrite orphans every
        // attached wisp and script.
        var capturedResourcesByDto = new Dictionary<string, IReadOnlyList<SkillResourceInput>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in result.ToSave ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Name) || dto.SourceNames is null || dto.SourceNames.Count == 0)
                continue;
            var captured = await CaptureResourceInputsAsync(dto.SourceNames, dto.Resources, ct);
            if (captured.Count > 0)
                capturedResourcesByDto[dto.Name.Trim()] = captured;
        }

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

            if (capturedResourcesByDto.TryGetValue(skill.Name, out var carriedResources))
            {
                await _skillStore!.SaveAsync(skill, carriedResources.ToList());
                _logger.LogDebug("DreamService: optimization saved improved skill '{Name}' with {Count} preserved resource(s)",
                    skill.Name, carriedResources.Count);
            }
            else
            {
                await _skillStore!.SaveAsync(skill);
                _logger.LogDebug("DreamService: optimization saved improved skill '{Name}'", skill.Name);
            }
            saved++;
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
    private async Task RunSkillGapDetectionPassAsync(CancellationToken ct)
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

        // Append feedback signals correlated with skill usage to surface gap evidence.
        // Sessions with negative feedback where no skill was matched are strong gap indicators;
        // sessions with positive feedback on ad-hoc (no-skill) responses are codification candidates.
        if (_feedbackStore is not null)
        {
            var recentFeedback = await _feedbackStore.QueryRecentAsync(
                since: DateTimeOffset.UtcNow.AddDays(-14),
                maxResults: 200);

            if (recentFeedback.Count > 0)
            {
                // Determine which sessions had a skill invoked
                var sessionsWithSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_skillUsageStore is not null)
                {
                    var usageEvents = await _skillUsageStore.QueryRecentAsync(
                        DateTimeOffset.UtcNow.AddDays(-14), maxResults: 10000);
                    foreach (var evt in usageEvents)
                        sessionsWithSkills.Add(evt.SessionId);
                }

                var negativeNoSkill = recentFeedback
                    .Where(fb => fb.SignalType is FeedbackSignalType.UserThumbsDown or FeedbackSignalType.Correction
                                 && !sessionsWithSkills.Contains(fb.SessionId))
                    .ToList();

                var positiveNoSkill = recentFeedback
                    .Where(fb => fb.SignalType == FeedbackSignalType.UserThumbsUp
                                 && !sessionsWithSkills.Contains(fb.SessionId))
                    .ToList();

                if (negativeNoSkill.Count > 0)
                {
                    userMessage.AppendLine("## Negative feedback on sessions with NO skill match (strong gap signals):");
                    userMessage.AppendLine("These sessions had quality problems and no existing skill was invoked — a new skill may have helped.");
                    foreach (var fb in negativeNoSkill)
                    {
                        var detail = string.IsNullOrWhiteSpace(fb.Detail) ? string.Empty : $" — \"{fb.Detail}\"";
                        userMessage.AppendLine($"- [{fb.SignalType}] session {fb.SessionId}: {fb.Summary}{detail}");
                    }
                    userMessage.AppendLine();
                    _logger.LogDebug(
                        "DreamService: skill gap — injected {Count} negative-no-skill feedback signal(s)",
                        negativeNoSkill.Count);
                }

                if (positiveNoSkill.Count > 0)
                {
                    userMessage.AppendLine("## Positive feedback on sessions with NO skill match (codification candidates):");
                    userMessage.AppendLine("The agent handled these well without a skill — consider codifying the approach into a reusable skill.");
                    foreach (var fb in positiveNoSkill)
                    {
                        var detail = string.IsNullOrWhiteSpace(fb.Detail) ? string.Empty : $" — \"{fb.Detail}\"";
                        userMessage.AppendLine($"- [{fb.SignalType}] session {fb.SessionId}: {fb.Summary}{detail}");
                    }
                    userMessage.AppendLine();
                    _logger.LogDebug(
                        "DreamService: skill gap — injected {Count} positive-no-skill feedback signal(s)",
                        positiveNoSkill.Count);
                }
            }
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

        var result = await InvokeDreamPassAsync<SkillGapResultDto>(
            "skill gap",
            _skillGapDirective ?? BuiltInSkillGapDirective,
            userMessage.ToString(),
            ct);
        if (result is null) return;
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
    /// Formats subject-time metadata keys as a compact suffix for the consolidation prompt
    /// (e.g. " subject=2019-06" or " subject=1995..2003"). Returns empty string when no
    /// subject-time metadata is present.
    /// </summary>
    internal static string FormatSubjectTimeForPrompt(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta is null) return string.Empty;
        if (meta.TryGetValue("subjectTime", out var t) && !string.IsNullOrWhiteSpace(t))
            return $" subject={t}";
        meta.TryGetValue("subjectTimeStart", out var s);
        meta.TryGetValue("subjectTimeEnd", out var e);
        if (!string.IsNullOrWhiteSpace(s) || !string.IsNullOrWhiteSpace(e))
            return $" subject={s ?? "?"}..{e ?? "?"}";
        return string.Empty;
    }

    /// <summary>
    /// Merges the well-known subject-time metadata keys across a set of source entries:
    /// "subjectTime" (point), "subjectTimeStart"/"subjectTimeEnd" (range). Returns null when
    /// no source carries any subject-time metadata. For overlapping values, prefers the most
    /// specific (longest string) on "subjectTime", widens the range on start/end.
    /// Non-subject-time metadata keys are not merged — they're entry-scoped and would
    /// conflict ambiguously across merges.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? MergeSubjectTimeMetadata(IReadOnlyList<MemoryEntry> sources)
    {
        if (sources.Count == 0) return null;

        string? point = null;
        string? start = null;
        string? end = null;

        foreach (var s in sources)
        {
            if (s.Metadata is null) continue;
            if (s.Metadata.TryGetValue("subjectTime", out var p) && !string.IsNullOrWhiteSpace(p))
            {
                if (point is null || p.Length > point.Length) point = p;
            }
            if (s.Metadata.TryGetValue("subjectTimeStart", out var ss) && !string.IsNullOrWhiteSpace(ss))
            {
                if (start is null || string.CompareOrdinal(ss, start) < 0) start = ss;
            }
            if (s.Metadata.TryGetValue("subjectTimeEnd", out var se) && !string.IsNullOrWhiteSpace(se))
            {
                if (end is null || string.CompareOrdinal(se, end) > 0) end = se;
            }
        }

        if (point is null && start is null && end is null) return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (point is not null) result["subjectTime"] = point;
        if (start is not null) result["subjectTimeStart"] = start;
        if (end is not null) result["subjectTimeEnd"] = end;
        return result;
    }

    /// <summary>
    /// Applies exponential (half-life) importance decay to memory entries that haven't been
    /// reinforced recently. Decay is calendar-time based — running the dream more or less
    /// frequently produces the same decay curve in calendar days. Keyed on
    /// <see cref="MemoryEntry.LastSeenAt"/> — the last real save-event merged into the entry
    /// — not on <see cref="MemoryEntry.UpdatedAt"/>, so dream housekeeping (rephrasing,
    /// recategorization, score adjustments) does not reset the decay clock.
    /// <para>
    /// For each entry past the grace period, we compute how many decay-eligible days have
    /// elapsed since the last time this entry was touched and multiply the importance by
    /// <c>0.5^(elapsedDays / HalfLifeDays)</c>. Because multiplicative decay composes
    /// (<c>0.5^(a/T) · 0.5^(b/T) == 0.5^((a+b)/T)</c>), splitting the decay across many
    /// small cycles or applying it in one large catch-up pass produces the same total
    /// calendar-time curve.
    /// </para>
    /// </summary>
    internal async Task RunImportanceDecayPassAsync(IReadOnlyList<MemoryEntry> entries)
    {
        var graceDays = _options.ImportanceDecayGraceDays;
        var halfLifeDays = _options.ImportanceDecayHalfLifeDays;
        var floor = _options.ImportanceDecayFloor;

        if (halfLifeDays <= 0)
            return; // decay disabled

        var now = DateTimeOffset.UtcNow;
        var decayed = 0;

        foreach (var entry in entries)
        {
            var daysSinceSeen = (now - entry.LastSeenAt).TotalDays;
            if (daysSinceSeen < graceDays) continue;
            if (entry.ImportanceScore <= floor) continue;

            // Proxy for "time since last decay was applied": UpdatedAt is bumped every time
            // the entry is saved (including by the prior decay pass), so for regularly-running
            // decay on an otherwise-untouched entry this tracks cycle-to-cycle elapsed time.
            // Bound by (daysSinceSeen - graceDays) so decay doesn't jump retroactively into
            // the grace window when grace first expires.
            var lastTouch = entry.UpdatedAt ?? entry.CreatedAt;
            var daysSinceLastTouch = Math.Max(0, (now - lastTouch).TotalDays);
            var eligibleElapsed = Math.Min(daysSinceSeen - graceDays, daysSinceLastTouch);
            if (eligibleElapsed <= 0) continue;

            var factor = (float)Math.Pow(0.5, eligibleElapsed / halfLifeDays);
            var newImportance = Math.Max(floor, entry.ImportanceScore * factor);
            if (newImportance >= entry.ImportanceScore) continue;

            // Bump UpdatedAt — this is the anchor the next decay pass uses to compute
            // elapsed time. Without this, successive passes would re-measure elapsed from
            // the original UpdatedAt and double-count time across cycles.
            var updated = entry with
            {
                ImportanceScore = newImportance,
                UpdatedAt = now
            };
            await _memory.SaveAsync(updated);
            decayed++;

            _logger.LogDebug(
                "DreamService: decayed importance for {Id} from {Old:F3} to {New:F3} (last seen {SeenDays:F1}d ago, elapsed {Elapsed:F2}d)",
                entry.Id, entry.ImportanceScore, newImportance, daysSinceSeen, eligibleElapsed);
        }

        if (decayed > 0)
            _logger.LogInformation("DreamService: importance decay pass — {Count} entries decayed", decayed);
    }

    /// <summary>
    /// Extracts episodic memories from the conversation log — discrete experiences, events,
    /// and interactions worth remembering as "what happened" rather than just distilled facts.
    /// Also reinforces existing episodes: if a concept reappears across sessions, its
    /// importance score increases and its summary is enriched with new context.
    /// Runs before memory mining so episodes exist before facts are distilled.
    /// Does NOT clear the log — that is deferred to <see cref="RunPreferenceInferencePassAsync"/>.
    /// </summary>
    private async Task RunEpisodeExtractionPassAsync(CancellationToken ct)
    {
        if (_conversationLog is null || !_options.EpisodeExtractionEnabled)
            return;

        var entries = await _conversationLog.ReadAllAsync();
        if (entries.Count == 0)
        {
            _logger.LogDebug("DreamService: episode extraction — no log entries; skipping");
            return;
        }

        _logger.LogInformation("DreamService: episode extraction pass — {Count} log entries to analyze", entries.Count);

        await RunPassAsync("episode extraction", async () =>
        {
            // Fetch existing episodic memories so the LLM can reinforce them
            var existingEpisodes = await _memory.SearchAsync(
                new MemorySearchCriteria(Category: "episodic", MaxResults: 100));

            var userMessage = new StringBuilder();
            userMessage.AppendLine("Review the following conversation log and extract episodic memories.");
            userMessage.AppendLine();

            if (existingEpisodes.Count > 0)
            {
                userMessage.AppendLine("## Existing episodic memories (reinforce if referenced in new conversations)");
                foreach (var ep in existingEpisodes)
                {
                    var importance = ep.Metadata?.GetValueOrDefault("importance") ?? "0.5";
                    var sessions = ep.Metadata?.GetValueOrDefault("source_sessions") ?? "";
                    userMessage.AppendLine($"- [{ep.Id}] (importance={importance}, sessions={sessions}, category={ep.Category}): {ep.Content}");
                }
                userMessage.AppendLine();
            }

            userMessage.AppendLine("## Conversation log");
            var bySession = entries
                .GroupBy(e => e.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (sessionId, sessionEntries) in bySession)
            {
                userMessage.AppendLine($"### Session: {sessionId}");
                foreach (var e in sessionEntries)
                    userMessage.AppendLine($"[{e.Role}] {e.Content}");
                userMessage.AppendLine();
            }

            var result = await InvokeDreamPassAsync<EpisodeExtractionResultDto>(
                "episode extraction",
                _episodeDirective ?? BuiltInEpisodeDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;
            var created = 0;
            var reinforced = 0;

            // Process reinforcements of existing episodes
            foreach (var dto in result?.ToUpdate ?? [])
            {
                if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Content))
                    continue;

                var existing = await _memory.GetAsync(dto.Id);
                if (existing is null)
                {
                    _logger.LogDebug("DreamService: episode reinforcement target {Id} not found; skipping", dto.Id);
                    continue;
                }

                var metadata = new Dictionary<string, string>(existing.Metadata ?? new Dictionary<string, string>());
                if (dto.Importance is not null)
                    metadata["importance"] = dto.Importance.Value.ToString("F2");
                if (dto.SourceSessions is not null)
                {
                    var existingSessions = metadata.GetValueOrDefault("source_sessions", "");
                    var allSessions = string.Join(",",
                        (existingSessions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        .Concat(dto.SourceSessions)
                        .Distinct());
                    metadata["source_sessions"] = allSessions;
                }

                var updated = existing with
                {
                    Content = dto.Content.Trim(),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow,
                    ReinforcementCount = existing.ReinforcementCount + 1,
                    Metadata = metadata,
                    ImportanceScore = Math.Clamp(dto.Importance ?? existing.ImportanceScore, 0f, 1f)
                };

                await _memory.SaveAsync(updated);
                reinforced++;
                _logger.LogDebug("DreamService: reinforced episode {Id} (importance={Importance}, reinforced={Count}×): {Content}",
                    dto.Id, metadata.GetValueOrDefault("importance", "?"), updated.ReinforcementCount, dto.Content);
            }

            // Process new episodes
            foreach (var dto in result?.ToSave ?? [])
            {
                if (string.IsNullOrWhiteSpace(dto.Content))
                    continue;

                var tags = new List<string>(dto.Tags ?? []);
                if (!tags.Contains("episodic", StringComparer.OrdinalIgnoreCase))
                    tags.Insert(0, "episodic");

                var metadata = new Dictionary<string, string>
                {
                    ["importance"] = (dto.Importance ?? 0.5f).ToString("F2"),
                    ["actor"] = dto.Actor?.Trim() ?? "system",
                    ["event_type"] = dto.EventType?.Trim() ?? "conversation"
                };
                if (dto.SourceSessions is { Count: > 0 })
                    metadata["source_sessions"] = string.Join(",", dto.SourceSessions);

                var category = string.IsNullOrWhiteSpace(dto.Category)
                    ? $"episodic/{metadata["event_type"]}"
                    : dto.Category.Trim();

                var entry = new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    Content: dto.Content.Trim(),
                    Category: category,
                    Tags: tags,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    Metadata: metadata,
                    ImportanceScore: Math.Clamp(dto.Importance ?? 0.5f, 0f, 1f));

                await _memory.SaveAsync(entry);
                created++;
                _logger.LogDebug("DreamService: created episode {Id} ({Category}, importance={Importance}): {Content}",
                    entry.Id, entry.Category, metadata["importance"], entry.Content);
            }

            _logger.LogInformation(
                "DreamService: episode extraction pass complete — {Created} created, {Reinforced} reinforced",
                created, reinforced);
        });
    }

    /// <summary>
    /// Extracts entities and relationships from episodic memories and conversation logs,
    /// populating the knowledge graph triple store for relational reasoning.
    /// </summary>
    private async Task RunEntityExtractionPassAsync(CancellationToken ct)
    {
        if (_knowledgeGraph is null || !_options.EntityExtractionEnabled)
            return;

        if (_conversationLog is null)
        {
            _logger.LogDebug("DreamService: entity extraction — no conversation log; skipping");
            return;
        }

        var entries = await _conversationLog.ReadAllAsync();
        if (entries.Count == 0)
        {
            _logger.LogDebug("DreamService: entity extraction — no log entries; skipping");
            return;
        }

        _logger.LogInformation("DreamService: entity extraction pass — {Count} log entries to analyze", entries.Count);

        await RunPassAsync("entity extraction", async () =>
        {
            // Provide existing entities so the LLM can reference/update them
            var existingEntities = await _knowledgeGraph.ListEntitiesAsync();

            var userMessage = new StringBuilder();
            userMessage.AppendLine("Review the following conversation log and extract entities and relationships.");
            userMessage.AppendLine();

            if (existingEntities.Count > 0)
            {
                userMessage.AppendLine("## Existing entities (reference these IDs when creating relationships)");
                foreach (var ent in existingEntities)
                {
                    var aliases = ent.Aliases.Count > 0 ? $" (aliases: {string.Join(", ", ent.Aliases)})" : "";
                    userMessage.AppendLine($"- [{ent.Id}] {ent.EntityType}: {ent.Name}{aliases}");
                }
                userMessage.AppendLine();
            }

            userMessage.AppendLine("## Conversation log");
            var bySession = entries
                .GroupBy(e => e.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (sessionId, sessionEntries) in bySession)
            {
                userMessage.AppendLine($"### Session: {sessionId}");
                foreach (var e in sessionEntries)
                    userMessage.AppendLine($"[{e.Role}] {e.Content}");
                userMessage.AppendLine();
            }

            var result = await InvokeDreamPassAsync<EntityExtractionResultDto>(
                "entity extraction",
                _entityExtractionDirective ?? BuiltInEntityExtractionDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;
            var entitiesCreated = 0;
            var triplesCreated = 0;

            foreach (var dto in result?.Entities ?? [])
            {
                if (string.IsNullOrWhiteSpace(dto.Name))
                    continue;

                var entityType = Enum.TryParse<KnowledgeEntityType>(dto.EntityType, ignoreCase: true, out var parsed)
                    ? parsed
                    : KnowledgeEntityType.Topic;

                var entity = new KnowledgeEntity(
                    Id: dto.Id ?? Guid.NewGuid().ToString("N")[..12],
                    Name: dto.Name.Trim(),
                    EntityType: entityType,
                    Aliases: dto.Aliases ?? [],
                    Metadata: dto.Metadata,
                    CreatedAt: DateTimeOffset.UtcNow);

                await _knowledgeGraph.SaveEntityAsync(entity);
                entitiesCreated++;
                _logger.LogDebug("DreamService: created entity {Id} ({Type}: {Name})",
                    entity.Id, entity.EntityType, entity.Name);
            }

            foreach (var dto in result?.Triples ?? [])
            {
                if (string.IsNullOrWhiteSpace(dto.Subject) ||
                    string.IsNullOrWhiteSpace(dto.Predicate) ||
                    string.IsNullOrWhiteSpace(dto.Object))
                    continue;

                var triple = new KnowledgeTriple(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    Subject: dto.Subject.Trim(),
                    Predicate: dto.Predicate.Trim(),
                    Object: dto.Object.Trim(),
                    Confidence: Math.Clamp(dto.Confidence ?? 0.5f, 0f, 1f),
                    SourceEpisodeId: dto.SourceEpisodeId,
                    CreatedAt: DateTimeOffset.UtcNow);

                await _knowledgeGraph.SaveTripleAsync(triple);
                triplesCreated++;
                _logger.LogDebug("DreamService: created triple {Id}: {Subject} --{Predicate}--> {Object}",
                    triple.Id, triple.Subject, triple.Predicate, triple.Object);
            }

            _logger.LogInformation(
                "DreamService: entity extraction pass complete — {Entities} entities, {Triples} triples created",
                entitiesCreated, triplesCreated);
        });
    }

    /// <summary>
    /// Reviews the knowledge graph for stale, redundant, or low-quality entries and asks the
    /// LLM to decide what to delete or merge. Runs after entity extraction so newly created
    /// entities are included in the review.
    /// </summary>
    private async Task RunGraphConsolidationPassAsync(CancellationToken ct)
    {
        if (_knowledgeGraph is null || !_options.GraphConsolidationEnabled)
            return;

        var entities = await _knowledgeGraph.ListEntitiesAsync();
        var triples = await _knowledgeGraph.ListTriplesAsync();

        if (entities.Count == 0 && triples.Count == 0)
        {
            _logger.LogDebug("DreamService: graph consolidation — empty graph; skipping");
            return;
        }

        _logger.LogInformation(
            "DreamService: graph consolidation pass — {Entities} entities, {Triples} triples to review",
            entities.Count, triples.Count);

        await RunPassAsync("graph consolidation", async () =>
        {
            var now = _clock.Now;
            var userMessage = new StringBuilder();
            userMessage.AppendLine("Review the following knowledge graph and identify entries to delete or merge.");
            userMessage.AppendLine($"Current date/time: {now:yyyy-MM-dd HH:mm:ss zzz}");
            userMessage.AppendLine();

            userMessage.AppendLine("## Entities");
            foreach (var e in entities)
            {
                var aliases = e.Aliases.Count > 0 ? $" (aliases: {string.Join(", ", e.Aliases)})" : "";
                var lastRef = e.LastReferencedAt.HasValue
                    ? $", lastReferenced={e.LastReferencedAt.Value:yyyy-MM-dd}"
                    : ", lastReferenced=never";
                var meta = e.Metadata is { Count: > 0 }
                    ? $", metadata={{{string.Join(", ", e.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}}}"
                    : "";
                userMessage.AppendLine(
                    $"- [{e.Id}] {e.EntityType}: {e.Name}{aliases} (created={e.CreatedAt:yyyy-MM-dd}{lastRef}{meta})");
            }
            userMessage.AppendLine();

            userMessage.AppendLine("## Triples");
            foreach (var t in triples)
            {
                var source = t.SourceEpisodeId is not null ? $", source={t.SourceEpisodeId}" : "";
                userMessage.AppendLine(
                    $"- [{t.Id}] {t.Subject} --{t.Predicate}--> {t.Object} (confidence={t.Confidence:F2}, created={t.CreatedAt:yyyy-MM-dd}{source})");
            }

            var result = await InvokeDreamPassAsync<GraphConsolidationResultDto>(
                "graph consolidation",
                _graphConsolidationDirective ?? BuiltInGraphConsolidationDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;
            var entitiesDeleted = 0;
            var triplesDeleted = 0;

            foreach (var id in result?.DeleteEntities ?? [])
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                await _knowledgeGraph.DeleteEntityAsync(id);
                entitiesDeleted++;
                _logger.LogDebug("DreamService: graph consolidation deleted entity {Id}", id);
            }

            foreach (var id in result?.DeleteTriples ?? [])
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                await _knowledgeGraph.DeleteTripleAsync(id);
                triplesDeleted++;
                _logger.LogDebug("DreamService: graph consolidation deleted triple {Id}", id);
            }

            _logger.LogInformation(
                "DreamService: graph consolidation pass complete — {EntitiesDeleted} entities deleted, {TriplesDeleted} triples deleted",
                entitiesDeleted, triplesDeleted);
        });
    }

    /// <summary>
    /// Scans the conversation log for factual observations, project context, and domain
    /// knowledge worth preserving in long-term memory. Complements preference inference
    /// (which targets behavioral patterns) and skill gap detection (which targets procedures).
    /// Does NOT clear the log — that is deferred to <see cref="RunPreferenceInferencePassAsync"/>.
    /// </summary>
    private async Task RunMemoryMiningPassAsync(CancellationToken ct)
    {
        if (_conversationLog is null || !_options.MemoryMiningEnabled)
            return;

        var entries = await _conversationLog.ReadAllAsync();
        if (entries.Count == 0)
        {
            _logger.LogDebug("DreamService: memory mining — no log entries; skipping");
            return;
        }

        _logger.LogInformation("DreamService: memory mining pass — {Count} log entries to analyze", entries.Count);

        await RunPassAsync("memory mining", async () =>
        {
            var userMessage = new StringBuilder();
            userMessage.AppendLine("Review the following conversation log for facts worth storing in long-term memory:");
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

            await AppendSubagentWhiteboardEntriesAsync(userMessage);

            var result = await InvokeDreamPassAsync<MemoryMiningResultDto>(
                "memory mining",
                _memoryMiningDirective ?? BuiltInMemoryMiningDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;
            var saved = 0;

            foreach (var dto in result?.ToSave ?? [])
            {
                if (string.IsNullOrWhiteSpace(dto.Content))
                    continue;

                var tags = new List<string>(dto.Tags ?? []);
                if (!tags.Contains("mined", StringComparer.OrdinalIgnoreCase))
                    tags.Insert(0, "mined");

                var entry = new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    Content: dto.Content.Trim(),
                    Category: string.IsNullOrWhiteSpace(dto.Category) ? "general" : dto.Category.Trim(),
                    Tags: tags,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow);

                await _memory.SaveAsync(entry);
                saved++;
                _logger.LogDebug("DreamService: memory mining saved {Id} ({Category}): {Content}",
                    entry.Id, entry.Category, entry.Content);
            }

            _logger.LogInformation("DreamService: memory mining pass complete — {Saved} entry(ies) saved", saved);
        });
    }

    /// <summary>
    /// One retry-until-success occurrence: within a single session, a tool was called multiple
    /// times with different argument values, with at least one failure followed by a success.
    /// </summary>
    internal sealed record ToolRetryPattern(
        string SessionId,
        string ToolName,
        IReadOnlyList<string> FailedArgs,
        string SuccessArgs,
        DateTimeOffset LastSeenAt);

    /// <summary>
    /// Default lookback window for retry-until-success detection. A successful tool call only
    /// counts as a retry resolution when at least one failed call with different args occurred
    /// within this time window before it. Tight enough to exclude unrelated activity in long-
    /// running sessions, loose enough to span multi-minute exploratory sequences.
    /// </summary>
    internal static readonly TimeSpan DefaultRetryLookbackWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Pure detection logic — extracts retry-until-success patterns from a set of tool-call
    /// events. For each (session, toolName) bucket, walks events in time order and, for each
    /// successful event, looks back <paramref name="lookbackWindow"/> for failed events with
    /// different argument values. The window is essential for long-running session IDs where
    /// the bucket can span weeks: a March success and an April failure don't form a retry pair.
    /// Patterns within a bucket are deduplicated by successArgs so the same lesson hit multiple
    /// times within one bucket emits one pattern with merged failed-arg context.
    /// </summary>
    internal static IReadOnlyList<ToolRetryPattern> DetectToolRetryPatternsFromEvents(
        IEnumerable<ToolCallEvent> events,
        HashSet<string>? sessionsFilter = null,
        TimeSpan? lookbackWindow = null)
    {
        var window = lookbackWindow ?? DefaultRetryLookbackWindow;

        var filtered = sessionsFilter is null
            ? events
            : events.Where(e => sessionsFilter.Contains(e.SessionId));

        var patterns = new List<ToolRetryPattern>();

        foreach (var group in filtered.GroupBy(e => $"{e.SessionId}\0{e.ToolName}"))
        {
            var ordered = group.OrderBy(e => e.Timestamp).ToList();
            if (ordered.Count < 2) continue;

            // bucketByArgs collapses repeated successes-with-the-same-args within a bucket
            // into one pattern, merging the in-window failed-arg context across occurrences.
            var bucketByArgs = new Dictionary<string, (List<string> FailedArgs, DateTimeOffset LastSeen)>(
                StringComparer.Ordinal);

            for (int i = 0; i < ordered.Count; i++)
            {
                var success = ordered[i];
                if (!success.Succeeded) continue;

                var successArgs = success.ArgumentsSummary ?? "(none)";
                var windowStart = success.Timestamp - window;

                // Walk backwards from this success; collect distinct different-args failures
                // until we exit the window or hit the cap.
                var windowFailures = new List<string>();
                for (int j = i - 1; j >= 0; j--)
                {
                    var prior = ordered[j];
                    if (prior.Timestamp < windowStart) break;
                    if (prior.Succeeded) continue;

                    var priorArgs = prior.ArgumentsSummary ?? "(none)";
                    if (string.Equals(priorArgs, successArgs, StringComparison.Ordinal)) continue;
                    if (!windowFailures.Contains(priorArgs, StringComparer.Ordinal))
                        windowFailures.Add(priorArgs);
                    if (windowFailures.Count >= 3) break;
                }

                if (windowFailures.Count == 0) continue;

                var truncatedFailures = windowFailures.Select(a => Truncate(a, 200)).ToList();

                if (bucketByArgs.TryGetValue(successArgs, out var existing))
                {
                    foreach (var f in truncatedFailures)
                        if (!existing.FailedArgs.Contains(f, StringComparer.Ordinal)
                            && existing.FailedArgs.Count < 3)
                            existing.FailedArgs.Add(f);
                    bucketByArgs[successArgs] = (existing.FailedArgs, success.Timestamp);
                }
                else
                {
                    bucketByArgs[successArgs] = (truncatedFailures, success.Timestamp);
                }
            }

            if (bucketByArgs.Count == 0) continue;

            var sessionId = ordered[0].SessionId;
            var toolName = ordered[0].ToolName;

            foreach (var kvp in bucketByArgs)
            {
                patterns.Add(new ToolRetryPattern(
                    sessionId,
                    toolName,
                    kvp.Value.FailedArgs,
                    Truncate(kvp.Key, 200),
                    kvp.Value.LastSeen));
            }
        }

        return patterns;
    }

    /// <summary>
    /// Scans the tool-call log for retry-until-success patterns. The differing argument value is
    /// the ambiguity that the guiding skill (or the agent's reasoning) failed to resolve up front,
    /// and is exactly what skill-optimize and tool-success-learning need to act on.
    /// </summary>
    /// <param name="sessionsFilter">If non-null, only events from these sessions are considered.</param>
    private async Task<IReadOnlyList<ToolRetryPattern>> DetectToolRetryPatternsAsync(
        DateTimeOffset since, HashSet<string>? sessionsFilter = null)
    {
        if (_toolCallLog is null)
            return [];

        try
        {
            var events = await _toolCallLog.QueryRecentAsync(since, maxResults: 20000);
            return events.Count == 0
                ? []
                : DetectToolRetryPatternsFromEvents(events, sessionsFilter);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DreamService: failed to scan tool-call log for retry patterns");
            return [];
        }
    }

    private async Task<Dictionary<string, List<string>>> DetectToolRetrySessionsAsync(
        HashSet<string> sessionsWithSkills, DateTimeOffset since)
    {
        var patterns = await DetectToolRetryPatternsAsync(since, sessionsWithSkills);
        return GroupRetryPatternsBySession(patterns);
    }

    /// <summary>
    /// Converts a flat list of retry patterns into per-session human-readable notes —
    /// the shape skill-optimize uses to inject ambiguity context per skill.
    /// </summary>
    internal static Dictionary<string, List<string>> GroupRetryPatternsBySession(
        IEnumerable<ToolRetryPattern> patterns)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in patterns)
        {
            if (!result.TryGetValue(p.SessionId, out var list))
            {
                list = [];
                result[p.SessionId] = list;
            }
            list.Add(FormatToolRetryNote(p));
        }
        return result;
    }

    /// <summary>
    /// Single-line description of one retry pattern, suitable for injection into a skill-
    /// optimization prompt as ambiguity context.
    /// </summary>
    internal static string FormatToolRetryNote(ToolRetryPattern p) =>
        $"Tool '{p.ToolName}' failed with args [{string.Join(" | ", p.FailedArgs)}] " +
        $"then succeeded with args [{p.SuccessArgs}]";

    /// <summary>
    /// Deduplicates retry patterns on (toolName, successArgs) keeping the most recent
    /// occurrence of each lesson, then caps at <paramref name="maxCount"/> so the prompt
    /// stays bounded even when a long tail of distinct lessons exists.
    /// </summary>
    internal static IReadOnlyList<ToolRetryPattern> DedupeRetryPatterns(
        IEnumerable<ToolRetryPattern> patterns, int maxCount)
    {
        return patterns
            .GroupBy(p => $"{p.ToolName}\0{p.SuccessArgs}")
            .Select(g => g.OrderByDescending(p => p.LastSeenAt).First())
            .Take(maxCount)
            .ToList();
    }

    /// <summary>
    /// Builds the user-message for the tool-success-learning pass from already-deduplicated
    /// patterns. The leading paragraph instructs the LLM what to extract; each pattern is
    /// formatted as a numbered section with failed args, successful args, and last-seen
    /// timestamp.
    /// </summary>
    internal static string BuildToolSuccessLearningUserMessage(
        IReadOnlyList<ToolRetryPattern> distinctPatterns)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "The following tool calls each followed a retry-until-success pattern within a single session. " +
            "Each entry shows the failed argument values followed by the value that succeeded. " +
            "For each one, extract the durable, verified fact the success proves about the external system " +
            "(e.g. which server holds a resource, which account ID maps to which calendar, which folder " +
            "path is correct). Skip patterns where the lesson is uninteresting or transient.");
        sb.AppendLine();

        var i = 1;
        foreach (var p in distinctPatterns)
        {
            sb.AppendLine($"### Pattern {i++}: tool '{p.ToolName}'");
            sb.AppendLine($"- Failed args: {string.Join(" | ", p.FailedArgs)}");
            sb.AppendLine($"- Successful args: {p.SuccessArgs}");
            sb.AppendLine($"- Last seen: {p.LastSeenAt:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalizes the LLM's tool-success-learning JSON into <see cref="MemoryEntry"/> values
    /// ready to persist: drops empty content, adds the canonical "verified" and
    /// "tool-success-learned" tags if missing, defaults the category to "tool-knowledge"
    /// when blank.
    /// </summary>
    internal static IReadOnlyList<MemoryEntry> NormalizeToolSuccessLearningEntries(
        MemoryMiningResultDto? result,
        Func<string> idFactory,
        Func<DateTimeOffset> nowFactory)
    {
        var entries = new List<MemoryEntry>();
        foreach (var dto in result?.ToSave ?? [])
        {
            if (string.IsNullOrWhiteSpace(dto.Content))
                continue;

            var tags = new List<string>(dto.Tags ?? []);
            if (!tags.Contains("verified", StringComparer.OrdinalIgnoreCase))
                tags.Add("verified");
            if (!tags.Contains("tool-success-learned", StringComparer.OrdinalIgnoreCase))
                tags.Add("tool-success-learned");

            var now = nowFactory();
            entries.Add(new MemoryEntry(
                Id: idFactory(),
                Content: dto.Content.Trim(),
                Category: string.IsNullOrWhiteSpace(dto.Category) ? "tool-knowledge" : dto.Category.Trim(),
                Tags: tags,
                CreatedAt: now,
                UpdatedAt: now));
        }
        return entries;
    }

    /// <summary>
    /// Builds the "Subagent verified data" section appended to the memory-mining input, or
    /// returns null when there are no entries to include. Caps each entry value at
    /// <paramref name="perEntryCap"/> chars and emits at most <paramref name="maxEntries"/>
    /// entries (newest first by <see cref="WorkingMemoryEntry.StoredAt"/>).
    /// </summary>
    internal static string? BuildSubagentWhiteboardSection(
        IReadOnlyList<WorkingMemoryEntry> entries, int perEntryCap, int maxEntries)
    {
        if (entries.Count == 0)
            return null;

        var ordered = entries
            .OrderByDescending(e => e.StoredAt)
            .Take(maxEntries)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("## Subagent verified data (working memory whiteboards)");
        sb.AppendLine("These entries were written by subagents based on tool-call results, so the");
        sb.AppendLine("facts in them are verified — not speculation. Mine them for durable facts");
        sb.AppendLine("about external systems (server names, account IDs, file paths, parameter shapes).");
        sb.AppendLine();

        foreach (var e in ordered)
        {
            var value = e.Value.Length > perEntryCap
                ? e.Value[..perEntryCap] + "…[truncated]"
                : e.Value;
            var category = string.IsNullOrEmpty(e.Category) ? "(uncategorized)" : e.Category;
            sb.AppendLine($"[{e.Key}] category={category}");
            sb.AppendLine(value);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Mines the tool-call log for retry-until-success patterns and asks the LLM to extract
    /// the verified fact each pattern proves (e.g. "Teams bridge JSON lives on
    /// onedrive-personal at /Apps/RockBot/xebia-teams"). Saves results as durable long-term
    /// memory entries tagged "verified" and "tool-success-learned" so future sessions surface
    /// them via BM25 or vector recall before the agent has to re-discover the same answer.
    /// </summary>
    private async Task RunToolSuccessLearningPassAsync(CancellationToken ct)
    {
        if (!_options.ToolSuccessLearningEnabled || _toolCallLog is null)
            return;

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var patterns = await DetectToolRetryPatternsAsync(since);
        if (patterns.Count == 0)
        {
            _logger.LogDebug("DreamService: tool-success-learning — no retry patterns found; skipping");
            return;
        }

        var distinctPatterns = DedupeRetryPatterns(patterns, maxCount: 50);

        _logger.LogInformation(
            "DreamService: tool-success-learning pass — {Distinct} distinct pattern(s) from {Total} occurrence(s)",
            distinctPatterns.Count, patterns.Count);

        var userMessage = BuildToolSuccessLearningUserMessage(distinctPatterns);

        await RunPassAsync("tool-success-learning", async () =>
        {
            var result = await InvokeDreamPassAsync<MemoryMiningResultDto>(
                "tool-success-learning",
                _toolSuccessLearningDirective ?? BuiltInToolSuccessLearningDirective,
                userMessage,
                ct);
            var entries = NormalizeToolSuccessLearningEntries(
                result,
                idFactory: () => Guid.NewGuid().ToString("N")[..12],
                nowFactory: () => DateTimeOffset.UtcNow);

            foreach (var entry in entries)
            {
                await _memory.SaveAsync(entry);
                _logger.LogDebug("DreamService: tool-success-learning saved {Id} ({Category}): {Content}",
                    entry.Id, entry.Category, entry.Content);
            }

            _logger.LogInformation(
                "DreamService: tool-success-learning pass complete — {Saved} entry(ies) saved", entries.Count);
        });
    }

    /// <summary>
    /// Appends recent subagent whiteboard entries (live working-memory entries with key prefix
    /// "subagent/") to the memory-mining input so the miner can see facts that subagents
    /// verified via tool calls but never restated in the conversation log. Entries are size-capped
    /// to keep the prompt bounded.
    /// </summary>
    private async Task AppendSubagentWhiteboardEntriesAsync(StringBuilder userMessage)
    {
        if (_workingMemory is null)
            return;

        try
        {
            var entries = await _workingMemory.ListAsync("subagent/");
            var section = BuildSubagentWhiteboardSection(entries, perEntryCap: 2000, maxEntries: 50);
            if (section is null)
                return;

            userMessage.Append(section);

            _logger.LogDebug(
                "DreamService: memory mining included {Count} subagent whiteboard entry(ies) in input",
                Math.Min(entries.Count, 50));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DreamService: failed to load subagent whiteboard entries for memory mining");
        }
    }

    /// <summary>
    /// Runs the observation framework pipeline (theory-of-self, theory-of-user,
    /// plus any other registered targets) against the conversation log.
    /// Promoted theories are published to long-term memory so they appear
    /// in <c>SearchMemory</c>; markdown copies are written to the agent
    /// profile's <c>observation/</c> subdirectory for human inspection.
    /// </summary>
    /// <remarks>
    /// Skipped silently when <see cref="DreamOptions.ObservationEnabled"/>
    /// is false or when the framework's services are not registered (no
    /// targets in DI, no coordinator available). Per-target failures are
    /// logged inside the coordinator and do not block other targets.
    /// MUST run before <see cref="RunPreferenceInferencePassAsync"/> because
    /// pref inference clears the conversation log in its finally block.
    /// </remarks>
    private async Task RunObservationPassAsync(CancellationToken ct)
    {
        if (!_options.ObservationEnabled)
        {
            _logger.LogInformation("DreamService: observation pass disabled by configuration");
            return;
        }
        if (_observationCoordinator is null || _observationTranscriptAdapter is null)
        {
            _logger.LogInformation(
                "DreamService: observation pass skipped — coordinator={Coordinator}, adapter={Adapter}",
                _observationCoordinator is not null, _observationTranscriptAdapter is not null);
            return;
        }

        await RunPassAsync("observation", async () =>
        {
            var transcripts = await _observationTranscriptAdapter
                .GetTranscriptAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "DreamService: observation pass starting — {TurnCount} transcript turns loaded",
                transcripts.Count);

            // Run the coordinator even when transcripts.Count == 0: the
            // evaluation phase still needs to age out stale candidates/theories
            // and regenerate the markdown so the operator sees current state.
            var results = await _observationCoordinator
                .RunAllAsync(transcripts, ct).ConfigureAwait(false);

            foreach (var r in results)
            {
                if (r.Failure is not null)
                {
                    _logger.LogWarning(
                        "DreamService: observation target {Target} failed: {Message}",
                        r.TargetName, r.Failure.Message);
                    continue;
                }

                _logger.LogInformation(
                    "DreamService: observation target {Target} — " +
                    "phase1: {Proposed} proposed/{Grounded} grounded → " +
                    "{NewCands} new candidate(s), {Matched} matched; " +
                    "phase2: {Eligible} evaluated, {Promoted} promoted, " +
                    "{Refined} refined, {Rejected} rejected, " +
                    "{CandsAged} candidates aged out, {ThriesAged} theories aged out",
                    r.TargetName,
                    r.ExtractionResult?.ProposalsReceived ?? 0,
                    r.ExtractionResult?.ProposalsGrounded ?? 0,
                    r.ExtractionResult?.NewCandidatesCreated ?? 0,
                    r.ExtractionResult?.MatchedExistingCandidates ?? 0,
                    r.EvaluationResult?.CandidatesEvaluated ?? 0,
                    r.EvaluationResult?.CandidatesPromoted ?? 0,
                    r.EvaluationResult?.CandidatesRefined ?? 0,
                    r.EvaluationResult?.CandidatesRejected ?? 0,
                    r.EvaluationResult?.CandidatesAged ?? 0,
                    r.EvaluationResult?.TheoriesAged ?? 0);
            }
        });
    }

    /// <summary>
    /// Analyzes the accumulated conversation log for durable user preference patterns
    /// and saves inferred preferences as tagged memory entries.
    /// Always clears the log after the pass to prevent unbounded growth.
    /// </summary>
    private async Task RunPreferenceInferencePassAsync(CancellationToken ct)
    {
        if (_conversationLog is null || !_options.PreferenceInferenceEnabled)
            return;

        var entries = await _conversationLog.ReadAllAsync();
        if (entries.Count == 0)
            return;

        _logger.LogInformation("DreamService: preference inference pass — {Count} log entries to analyze", entries.Count);

        try
        {
            await RunPassAsync("preference inference", async () =>
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

            var result = await InvokeDreamPassAsync<PrefDreamResultDto>(
                "preference inference",
                _prefDreamDirective ?? BuiltInPrefDirective,
                userMessage.ToString(),
                ct);
            if (result is not null)
            {
                var saved = 0;

                foreach (var dto in result.ToSave ?? [])
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
            });
        }
        finally
        {
            // Always clear the log regardless of LLM success/failure to prevent unbounded growth
            await _conversationLog.ClearAsync();
            _logger.LogDebug("DreamService: conversation log cleared after preference inference pass");
        }
    }

    /// <summary>
    /// Reviews recent tier-routing decisions and writes an updated <c>tier-selector.json</c>
    /// when the LLM detects systematic mis-routing. Skipped when fewer than 10 entries exist.
    /// </summary>
    private async Task RunTierRoutingReviewPassAsync(CancellationToken ct)
    {
        if (_tierRoutingLogger is null || !_options.TierRoutingReviewEnabled)
            return;

        // Pinned at 200 even though the cap is now configurable (default 1500). The analyzer's
        // prompt cost is independent of entry count, so we could read more — but more entries
        // also mean more file-IO and more cluster permutations, and 200 has been shown to
        // surface every detection rule reliably. Bump if needed; do not silently widen.
        var entries = await _tierRoutingLogger.ReadRecentAsync(200);
        if (entries.Count < 10)
        {
            _logger.LogDebug(
                "DreamService: tier routing review — only {Count} entries; skipping (need ≥ 10)",
                entries.Count);
            return;
        }

        _logger.LogInformation(
            "DreamService: tier routing review pass — {Count} routing entries",
            entries.Count);

        var configPath = ResolvePath("tier-selector.json", _profileOptions.BasePath);
        TierSelectorConfig? currentConfig = null;
        if (File.Exists(configPath))
        {
            try
            {
                var configJson = await File.ReadAllTextAsync(configPath);
                currentConfig = JsonSerializer.Deserialize<TierSelectorConfig>(configJson, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DreamService: failed to parse tier-selector.json at {Path}", configPath);
            }
        }

        // Tier→model map lets the analyzer compute per-tier USD cost deltas on threshold scans
        // and flagged clusters. Without it those deltas are null and the LLM has no cost signal
        // to weigh against quality — which is how balancedCeiling drifted to its floor. The
        // High cost floor (see DreamOptions) ensures a Balanced→High shift never projects as
        // zero-cost when High currently shares Balanced's model.
        IReadOnlyDictionary<ModelTier, string?>? tierModelMap = _tieredRegistry is null
            ? null
            : new Dictionary<ModelTier, string?>
            {
                [ModelTier.Low]      = _tieredRegistry.GetModelId(ModelTier.Low),
                [ModelTier.Balanced] = _tieredRegistry.GetModelId(ModelTier.Balanced),
                [ModelTier.High]     = _tieredRegistry.GetModelId(ModelTier.High),
            };

        // Pre-aggregate the raw entries into a structured analysis so the LLM
        // works against deterministic statistics instead of recomputing them.
        // This decouples prompt size from entry count — N entries collapse to ~M clusters.
        var analysis = TierRoutingAnalyzer.Analyze(
            entries, currentConfig, _pricingRows, tierModelMap,
            _options.TierRoutingHighCostFloorMultiplier);

        var analysisJson = JsonSerializer.Serialize(analysis, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        var userMessage = new StringBuilder();
        userMessage.AppendLine("Pre-aggregated tier-routing analysis for review:");
        userMessage.AppendLine();
        userMessage.AppendLine(analysisJson);

        if (currentConfig is not null)
        {
            userMessage.AppendLine();
            userMessage.AppendLine("Current tier-selector.json:");
            userMessage.AppendLine(JsonSerializer.Serialize(currentConfig, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
        }

        var result = await InvokeDreamPassAsync<TierRoutingReviewResultDto>(
            "tier routing review",
            _tierRoutingDirective ?? BuiltInTierRoutingDirective,
            userMessage.ToString(),
            ct);
        if (result is null) return;

        // Save anti-pattern entries regardless of whether the config changed
        if (result.AntiPatterns is { Count: > 0 })
        {
            var savedAntiPatterns = 0;
            foreach (var ap in result.AntiPatterns)
            {
                if (string.IsNullOrWhiteSpace(ap.Content)) continue;

                var content = string.IsNullOrWhiteSpace(ap.Detail)
                    ? ap.Content.Trim()
                    : $"{ap.Content.Trim()}\n\nDetail: {ap.Detail.Trim()}";

                var entry = new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    Content: content,
                    Category: "anti-patterns/routing",
                    Tags: ["routing", "anti-pattern", "dream"],
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow);

                await _memory.SaveAsync(entry);
                savedAntiPatterns++;
                _logger.LogInformation(
                    "DreamService: tier routing review saved anti-pattern entry: {Content}",
                    ap.Content);
            }
            _logger.LogInformation(
                "DreamService: tier routing review — {Count} anti-pattern entry(ies) saved",
                savedAntiPatterns);
        }

        if (result.NoChangeNeeded == true)
        {
            _logger.LogInformation("DreamService: tier routing review — no config change needed");
            return;
        }

        if (result.Config is null)
        {
            _logger.LogWarning("DreamService: tier routing review — LLM said change needed but returned no config; skipping");
            return;
        }

        // Deterministic ratchet-stop (nothing trusts the LLM): never apply a balancedCeiling
        // DECREASE while the observed High-tier routing share already exceeds the target.
        var highPct = analysis.GlobalStats.ByTier
            .FirstOrDefault(t => t.Tier == ModelTier.High)?.Pct ?? 0.0;
        result.Config.BalancedCeiling = GuardBalancedCeilingDecrease(
            result.Config.BalancedCeiling, currentConfig?.BalancedCeiling,
            highPct, _options.TierRoutingHighTargetPct, _logger);

        var writeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(result.Config, writeOptions));

        _logger.LogInformation(
            "DreamService: tier routing review updated tier-selector.json — notes: {Notes}",
            result.Config.Notes ?? "(none)");
    }

    /// <summary>
    /// Deterministic ratchet-stop for the tier-routing review pass. Returns the balancedCeiling
    /// that should actually be written: the LLM's <paramref name="proposed"/> value, EXCEPT a
    /// DECREASE is rejected (held at <paramref name="current"/>) while the observed High-tier
    /// routing share exceeds <paramref name="targetPct"/>. A lower balancedCeiling pushes more
    /// traffic into High, so honoring such a proposal while already over budget is the exact
    /// drift this guard prevents. The cost floor makes the LLM unlikely to propose it; this
    /// enforces it in code regardless. Exposed as <c>internal static</c> for unit testing.
    /// </summary>
    internal static double? GuardBalancedCeilingDecrease(
        double? proposed, double? current, double highPct, double targetPct, ILogger logger)
    {
        if (highPct > targetPct
            && proposed is double p && current is double c && p < c)
        {
            logger.LogWarning(
                "DreamService: tier routing review — rejecting balancedCeiling decrease {From}→{To}; " +
                "High routing share {HighPct:F1}% exceeds target {Target:F1}% (held at current)",
                c, p, highPct, targetPct);
            return c;
        }
        return proposed;
    }

    // ── Built-in sequence skill directive ────────────────────────────────────
    private const string BuiltInSequenceSkillDirective = """
        You are a procedural skill synthesis assistant. Analyze the tool-call sequences
        provided and identify repeated multi-step workflows (2+ tools, 3+ sessions).
        Return a JSON object with a "toSave" array of skill objects, each with
        "name", "summary", and "content" fields. If no patterns found: { "toSave": [] }
        """;

    /// <summary>
    /// Analyzes tool-call sequences across recent sessions to detect repeated action patterns
    /// and synthesize them into reusable skills. Requires <see cref="IToolCallLog"/> and
    /// <see cref="ISkillStore"/> to be available.
    /// </summary>
    private async Task RunSequenceSkillDetectionPassAsync(CancellationToken ct)
    {
        if (_toolCallLog is null || _skillStore is null || !_options.SequenceSkillDetectionEnabled)
            return;

        await RunPassAsync("sequence skill detection", async () =>
        {
            var events = await _toolCallLog.QueryRecentAsync(
                DateTimeOffset.UtcNow.AddDays(-14), maxResults: 10_000);

            if (events.Count < 10)
            {
                _logger.LogDebug(
                    "DreamService: sequence skill detection — only {Count} tool-call events; skipping",
                    events.Count);
                return;
            }

            _logger.LogInformation(
                "DreamService: sequence skill detection pass — {Count} tool-call events",
                events.Count);

            // Group by session and build per-session tool sequences
            var sessions = events
                .GroupBy(e => e.SessionId)
                .Where(g => g.Count() >= 2) // Only sessions with 2+ tool calls
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Timestamp).ToList());

            if (sessions.Count < 3)
            {
                _logger.LogDebug("DreamService: sequence skill detection — fewer than 3 multi-tool sessions; skipping");
                return;
            }

            // Get existing skill names to avoid duplicates
            var existingSkills = await _skillStore.ListAsync();
            var existingNames = existingSkills.Select(s => s.Name).ToList();

            // Build prompt with session sequences
            var userMessage = new StringBuilder();
            userMessage.AppendLine($"Tool-call sequences from {sessions.Count} recent sessions:");
            userMessage.AppendLine();

            foreach (var (sessionId, calls) in sessions.Take(50)) // Cap at 50 sessions
            {
                userMessage.AppendLine($"Session {sessionId} ({calls.Count} calls):");
                foreach (var call in calls)
                {
                    var status = call.Succeeded ? "ok" : "failed";
                    var args = string.IsNullOrEmpty(call.ArgumentsSummary) ? "" : $" args=[{call.ArgumentsSummary}]";
                    userMessage.AppendLine($"  {call.ToolName}{args} → {status} ({call.DurationMs}ms)");
                }
                userMessage.AppendLine();
            }

            if (existingNames.Count > 0)
            {
                userMessage.AppendLine("Existing skills (do not duplicate):");
                foreach (var name in existingNames)
                    userMessage.AppendLine($"  - {name}");
            }

            var result = await InvokeDreamPassAsync<SequenceSkillResultDto>(
                "sequence skill detection",
                _sequenceSkillDirective ?? BuiltInSequenceSkillDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;
            var created = 0;

            foreach (var dto in result?.ToSave ?? [])
            {
                if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Content))
                    continue;

                // Skip if skill already exists
                var existing = await _skillStore.GetAsync(dto.Name);
                if (existing is not null)
                {
                    _logger.LogDebug("DreamService: sequence skill '{Name}' already exists; skipping", dto.Name);
                    continue;
                }

                var skill = new Skill(
                    Name: dto.Name.Trim(),
                    Summary: dto.Summary?.Trim() ?? dto.Name,
                    Content: dto.Content.Trim(),
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow);

                await _skillStore.SaveAsync(skill);
                created++;
                _logger.LogInformation(
                    "DreamService: sequence skill detection created skill '{Name}': {Summary}",
                    skill.Name, skill.Summary);
            }

            _logger.LogInformation(
                "DreamService: sequence skill detection pass complete — {Created} skills created from {SessionCount} sessions",
                created, sessions.Count);
        });
    }

    // ── Wisp failure analysis ─────────────────────────────────────────────

    private const string BuiltInWispFailureDirective = """
        You are analyzing wisp pipeline execution records to identify recurring failure patterns.
        Wisps are lightweight multi-step pipelines with tool invocations. Each record shows whether
        the wisp succeeded or failed, which step failed, and the failure classification.

        Analyze the provided records and respond with a JSON object containing:
        {
          "patterns": [
            {
              "description": "Human-readable description of the recurring pattern",
              "failureCategory": "Structural|External|Data|Judgment",
              "frequency": 3,
              "affectedSteps": ["step_id_1"],
              "recommendation": "What to change in the generating skill or tool usage"
            }
          ],
          "skillUpdates": [
            {
              "name": "skill-name-to-update",
              "annotation": "Negative example or correction to append to the skill content"
            }
          ]
        }

        Only include patterns with frequency >= 3. Only include skill updates when you are confident
        the correction is valid. Return empty arrays if no patterns are found.
        Successful patterns worth saving as reusable assets are handled by the separate
        wisp-success-dream pass — do not surface them here.
        """;

    /// <summary>
    /// Analyzes wisp execution records to detect recurring failure patterns and propose
    /// skill corrections. Requires <see cref="IWispExecutionLog"/> and <see cref="ISkillStore"/>.
    /// </summary>
    private async Task RunWispFailureAnalysisPassAsync(CancellationToken ct)
    {
        if (_wispExecutionLog is null || _skillStore is null || !_options.WispFailureAnalysisEnabled)
            return;

        await RunPassAsync("wisp failure analysis", async () =>
        {
            var records = await _wispExecutionLog.QueryRecentAsync(
                DateTimeOffset.UtcNow.AddDays(-14), maxResults: 500);

            if (records.Count < 5)
            {
                _logger.LogDebug(
                    "DreamService: wisp failure analysis — only {Count} records; skipping",
                    records.Count);
                return;
            }

            _logger.LogInformation(
                "DreamService: wisp failure analysis pass — {Count} records ({Failures} failures)",
                records.Count, records.Count(r => !r.Succeeded));

            // Build prompt with execution records summary
            var userMessage = new StringBuilder();
            userMessage.AppendLine($"Wisp execution records from the last 14 days ({records.Count} total):");
            userMessage.AppendLine();

            // Group by description for pattern detection
            var byDescription = records
                .GroupBy(r => r.Description)
                .OrderByDescending(g => g.Count(r => !r.Succeeded))
                .Take(30);

            foreach (var group in byDescription)
            {
                var total = group.Count();
                var failures = group.Count(r => !r.Succeeded);
                var corrections = group.Count(r => r.RetryOf is not null);
                userMessage.AppendLine($"### \"{group.Key}\" ({total} runs, {failures} failures, {corrections} corrections)");

                foreach (var record in group.OrderByDescending(r => r.Timestamp).Take(5))
                {
                    var status = record.Succeeded ? "ok" : $"FAILED ({record.FailureCategory})";
                    var step = record.FailedStepId is not null ? $" at step '{record.FailedStepId}'" : "";
                    var error = record.ErrorMessage is not null ? $" — {record.ErrorMessage}" : "";
                    var retry = record.RetryOf is not null ? " [retry]" : "";
                    userMessage.AppendLine($"  - {status}{step}{error}{retry} ({record.DurationMs}ms)");
                }
                userMessage.AppendLine();
            }

            // Include existing skill names for cross-reference
            var existingSkills = await _skillStore.ListAsync();
            if (existingSkills.Count > 0)
            {
                userMessage.AppendLine("Existing skills:");
                foreach (var skill in existingSkills.Take(30))
                    userMessage.AppendLine($"  - {skill.Name}: {skill.Summary}");
            }

            var result = await InvokeDreamPassAsync<WispFailureAnalysisResultDto>(
                "wisp failure analysis",
                _wispFailureDirective ?? BuiltInWispFailureDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;
            var updated = 0;

            // Apply skill updates
            foreach (var update in result?.SkillUpdates ?? [])
            {
                if (string.IsNullOrWhiteSpace(update.Name) || string.IsNullOrWhiteSpace(update.Annotation))
                    continue;

                var existing = await _skillStore.GetAsync(update.Name);
                if (existing is null)
                {
                    _logger.LogDebug("DreamService: wisp failure analysis — skill '{Name}' not found; skipping update", update.Name);
                    continue;
                }

                var annotatedContent = existing.Content + $"\n\n## Wisp Failure Pattern\n\n{update.Annotation}";
                var updatedSkill = existing with
                {
                    Content = annotatedContent,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await _skillStore.SaveAsync(updatedSkill);
                updated++;
                _logger.LogInformation(
                    "DreamService: wisp failure analysis annotated skill '{Name}' with failure pattern",
                    update.Name);
            }

            // Log patterns and promotion candidates
            foreach (var pattern in result?.Patterns ?? [])
            {
                _logger.LogInformation(
                    "DreamService: wisp failure pattern — {Description} (category={Category}, freq={Frequency}): {Recommendation}",
                    pattern.Description, pattern.FailureCategory, pattern.Frequency, pattern.Recommendation);
            }

            _logger.LogInformation(
                "DreamService: wisp failure analysis pass complete — {Patterns} patterns, {Updates} skill updates",
                result?.Patterns?.Count ?? 0, updated);
        });
    }

    private sealed record WispFailureAnalysisResultDto
    {
        public List<WispPatternDto>? Patterns { get; init; }
        public List<WispSkillUpdateDto>? SkillUpdates { get; init; }
    }

    private sealed record WispPatternDto
    {
        public string? Description { get; init; }
        public string? FailureCategory { get; init; }
        public int Frequency { get; init; }
        public List<string>? AffectedSteps { get; init; }
        public string? Recommendation { get; init; }
    }

    private sealed record WispSkillUpdateDto
    {
        public string? Name { get; init; }
        public string? Annotation { get; init; }
    }

    /// <summary>
    /// Symmetric complement to <see cref="RunWispFailureAnalysisPassAsync"/>. Detects wisp
    /// definitions that have repeated successfully across distinct sessions and promotes them
    /// to validated skill resources via <see cref="ISkillStore.AttachResourceAsync"/>.
    /// Promotions land non-provisional because the dream pass operates on observed repetition;
    /// the in-session promotion path (<c>promote_skill_asset</c>) is the one that lands provisional.
    /// </summary>
    private async Task RunWispSuccessAnalysisPassAsync(CancellationToken ct)
    {
        if (!_options.WispSuccessAnalysisEnabled || _wispExecutionLog is null || _skillStore is null)
            return;

        await RunPassAsync("wisp success analysis", async () =>
        {
            var threshold = Math.Max(1, _options.WispSuccessFrequencyThreshold);
            var records = await _wispExecutionLog.QueryRecentAsync(
                DateTimeOffset.UtcNow.AddDays(-7), maxResults: 1000);

            if (records.Count < threshold)
            {
                _logger.LogDebug(
                    "DreamService: wisp success analysis — only {Count} records; skipping",
                    records.Count);
                return;
            }

            // Group by ShapeHash when present (collapses runs that differ only by
            // description text or per-run literal values such as dates and account
            // ids), falling back to DefinitionHash for legacy records written before
            // the shape hash existed. Keep groups where every recorded run
            // succeeded and the cumulative count meets the threshold. Tighter than
            // the failure pass intentionally — we want zero false positives.
            var groups = records
                .Where(r => !string.IsNullOrEmpty(r.ShapeHash) || !string.IsNullOrEmpty(r.DefinitionHash))
                .GroupBy(r => !string.IsNullOrEmpty(r.ShapeHash) ? r.ShapeHash! : r.DefinitionHash)
                .Where(g => g.Count() >= threshold && g.All(r => r.Succeeded))
                .ToList();

            if (groups.Count == 0)
            {
                _logger.LogDebug("DreamService: wisp success analysis — no candidate groups; skipping");
                return;
            }

            // Resolve invokingSkill for each group: the most recently invoked skill in
            // any contributing session whose timestamp precedes that session's wisp run.
            var existingSkills = await _skillStore.ListAsync();
            var existingSkillNames = existingSkills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<WispSuccessCandidate>();
            foreach (var group in groups)
            {
                var hash = group.Key!;
                var representative = group.First();
                string? invokingSkill = null;

                if (_skillUsageStore is not null)
                {
                    foreach (var record in group.Where(r => !string.IsNullOrEmpty(r.SessionId)).OrderByDescending(r => r.Timestamp))
                    {
                        var events = await _skillUsageStore.GetBySessionAsync(record.SessionId!, ct);
                        var beforeWisp = events
                            .Where(e => e.Timestamp <= record.Timestamp && existingSkillNames.Contains(e.SkillName))
                            .OrderByDescending(e => e.Timestamp)
                            .FirstOrDefault();
                        if (beforeWisp is not null)
                        {
                            invokingSkill = beforeWisp.SkillName;
                            break;
                        }
                    }
                }

                if (invokingSkill is null)
                    continue;  // no target skill — cannot promote

                // Recover canonical body from any record in the group that retained
                // one — prefer the oldest successful body so the saved asset matches
                // the earliest verified shape. Falls back to the log lookup (keyed
                // on DefinitionHash) when the in-memory records don't carry a body.
                var body = group
                    .Where(r => r.Succeeded && !string.IsNullOrEmpty(r.DefinitionBody))
                    .OrderBy(r => r.Timestamp)
                    .Select(r => r.DefinitionBody)
                    .FirstOrDefault();
                if (string.IsNullOrEmpty(body))
                    body = await _wispExecutionLog.GetCanonicalBodyAsync(representative.DefinitionHash, ct);
                if (string.IsNullOrEmpty(body))
                    continue;  // body unavailable (oversize / pre-Phase-1 record)

                candidates.Add(new WispSuccessCandidate(
                    DefinitionHash: hash,
                    Frequency: group.Count(),
                    DistinctSessions: group.Select(r => r.SessionId).Where(s => !string.IsNullOrEmpty(s)).Distinct().Count(),
                    Description: representative.Description,
                    InvokingSkill: invokingSkill,
                    Body: body));
            }

            if (candidates.Count == 0)
            {
                _logger.LogDebug("DreamService: wisp success analysis — no resolvable candidates; skipping");
                return;
            }

            _logger.LogInformation(
                "DreamService: wisp success analysis pass — {Count} candidates",
                candidates.Count);

            // Build the prompt
            var userMessage = new StringBuilder();
            userMessage.AppendLine($"Successful wisp pattern candidates ({candidates.Count}):");
            userMessage.AppendLine();
            foreach (var c in candidates)
            {
                userMessage.AppendLine($"### definitionHash: {c.DefinitionHash}");
                userMessage.AppendLine($"- frequency: {c.Frequency}");
                userMessage.AppendLine($"- distinctSessions: {c.DistinctSessions}");
                userMessage.AppendLine($"- description: {c.Description}");
                userMessage.AppendLine($"- invokingSkill: {c.InvokingSkill}");
                var preview = c.Body.Length > 1024 ? c.Body[..1024] + "...(truncated)" : c.Body;
                userMessage.AppendLine($"- bodyPreview:");
                userMessage.AppendLine("```json");
                userMessage.AppendLine(preview);
                userMessage.AppendLine("```");
                userMessage.AppendLine();
            }
            userMessage.AppendLine("Existing skills (eligible promotion targets):");
            foreach (var s in existingSkills.Take(40))
                userMessage.AppendLine($"  - {s.Name}: {s.Summary}");

            var result = await InvokeDreamPassAsync<WispSuccessAnalysisResultDto>(
                "wisp success analysis",
                _wispSuccessDirective ?? BuiltInWispSuccessDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;

            var attached = await ApplyWispSuccessPromotionsAsync(
                _skillStore, candidates, existingSkillNames, result.Promotions, _logger, ct);

            _logger.LogInformation(
                "DreamService: wisp success analysis pass complete — {Attached}/{Total} promotions attached",
                attached, result.Promotions?.Count ?? 0);
        });
    }

    /// <summary>
    /// Apply loop extracted as a static helper so unit tests can drive the attach
    /// logic without standing up the full DreamService + LLM stack.
    /// </summary>
    internal static async Task<int> ApplyWispSuccessPromotionsAsync(
        ISkillStore skillStore,
        IReadOnlyList<WispSuccessCandidate> candidates,
        HashSet<string> existingSkillNames,
        IReadOnlyList<WispSuccessPromotionDto>? promotions,
        Microsoft.Extensions.Logging.ILogger logger,
        CancellationToken ct)
    {
        if (promotions is null || promotions.Count == 0)
            return 0;

        var attached = 0;
        foreach (var promotion in promotions)
        {
            if (string.IsNullOrWhiteSpace(promotion.TargetSkill)
                || string.IsNullOrWhiteSpace(promotion.Filename)
                || string.IsNullOrWhiteSpace(promotion.DefinitionHash))
                continue;

            if (!existingSkillNames.Contains(promotion.TargetSkill))
            {
                logger.LogDebug(
                    "DreamService: wisp success — target skill '{Skill}' not found; skipping promotion of {Hash}",
                    promotion.TargetSkill, promotion.DefinitionHash);
                continue;
            }

            var candidate = candidates.FirstOrDefault(c => c.DefinitionHash == promotion.DefinitionHash);
            if (candidate is null)
            {
                logger.LogDebug(
                    "DreamService: wisp success — promotion references unknown hash {Hash}; skipping",
                    promotion.DefinitionHash);
                continue;
            }

            var resourceType = promotion.ResourceType ?? SkillResourceType.Wisp;
            var description = string.IsNullOrWhiteSpace(promotion.Description)
                ? candidate.Description
                : promotion.Description!;

            // Pre-build the manifest entry so Provisional=false (dream-pass promotions
            // are observed-repetition validated, not hypotheses) and DefinitionHash
            // matches the source wisp record's hash.
            var entry = new SkillResource(
                promotion.Filename!, resourceType, description,
                Provisional: false,
                CreatedAt: DateTimeOffset.UtcNow,
                VerifyHint: null,
                DefinitionHash: candidate.DefinitionHash);
            var input = new SkillResourceInput(
                promotion.Filename!, resourceType, description, candidate.Body,
                Provisional: false);

            var ok = await skillStore.AttachResourceAsync(promotion.TargetSkill!, input, entry);
            if (ok)
            {
                attached++;
                logger.LogInformation(
                    "DreamService: wisp success — attached '{Filename}' to '{Skill}' (hash={Hash}, freq={Freq})",
                    promotion.Filename, promotion.TargetSkill, promotion.DefinitionHash, candidate.Frequency);
            }
        }

        return attached;
    }

    internal sealed record WispSuccessCandidate(
        string DefinitionHash,
        int Frequency,
        int DistinctSessions,
        string Description,
        string InvokingSkill,
        string Body);

    internal sealed record WispSuccessAnalysisResultDto
    {
        public List<WispSuccessPromotionDto>? Promotions { get; init; }
    }

    internal sealed record WispSuccessPromotionDto
    {
        public string? TargetSkill { get; init; }
        public string? Filename { get; init; }
        public SkillResourceType? ResourceType { get; init; }
        public string? Description { get; init; }
        public string? DefinitionHash { get; init; }
    }

    private const string BuiltInWispSuccessDirective = """
        You are analyzing wisp pipeline executions to identify successful patterns
        worth promoting to skill resources. Each candidate group shares the same
        definitionHash and has succeeded repeatedly with no failures.

        Respond with JSON:
        {
          "promotions": [
            {
              "targetSkill": "<existing skill name>",
              "filename": "<short-descriptive-filename.json>",
              "resourceType": "Wisp",
              "description": "<one line>",
              "definitionHash": "<hash from candidate>"
            }
          ]
        }

        Filter zero false positives over recall. Skip candidates whose invokingSkill
        is null or whose target skill is not in the existing-skills list. Empty
        promotions array is a fine answer.
        """;

    // ── Phase 5: Provisional resource validation/demotion ───────────────────────

    /// <summary>
    /// Sweeps every provisional skill resource and decides what to do with it
    /// based on how it has fared since it was attached. Wisp resources have a
    /// strong signal — wisp execution records sharing the resource's
    /// <see cref="SkillResource.DefinitionHash"/> count as direct success/failure.
    /// Non-wisp resources fall back to checkout events from
    /// <see cref="ISkillResourceUsageStore"/> as a soft positive signal.
    ///
    /// The decision is delegated to the static <see cref="DecideProvisionalAction"/>
    /// helper so unit tests can drive it without standing up the full DreamService stack.
    /// </summary>
    private async Task RunProvisionalValidationPassAsync(CancellationToken ct)
    {
        if (!_options.ProvisionalValidationEnabled || _skillStore is null || _wispExecutionLog is null)
            return;

        await RunPassAsync("provisional resource validation", async () =>
        {
            var skills = await _skillStore.ListAsync();
            var provisional = skills
                .Where(s => s.Manifest is not null)
                .SelectMany(s => s.Manifest!.Where(r => r.Provisional).Select(r => (s.Name, Resource: r)))
                .ToList();

            if (provisional.Count == 0)
            {
                _logger.LogDebug("DreamService: provisional validation — no provisional resources; skipping");
                return;
            }

            _logger.LogInformation(
                "DreamService: provisional validation pass — {Count} provisional resource(s)",
                provisional.Count);

            var promoted = 0;
            var removed = 0;
            var staled = 0;
            var now = DateTimeOffset.UtcNow;

            foreach (var (skillName, resource) in provisional)
            {
                var since = resource.CreatedAt ?? DateTimeOffset.UtcNow.AddDays(-30);

                IReadOnlyList<WispExecutionRecord> wispRecords = [];
                if (!string.IsNullOrEmpty(resource.DefinitionHash))
                {
                    // Match new shape-hashed resources against either ShapeHash (preferred)
                    // or DefinitionHash (legacy resources written with a body hash).
                    var allRecords = await _wispExecutionLog.QueryRecentAsync(since, maxResults: 1000, ct);
                    wispRecords = allRecords.Where(r =>
                        r.ShapeHash == resource.DefinitionHash
                        || r.DefinitionHash == resource.DefinitionHash).ToList();
                }

                IReadOnlyList<SkillResourceCheckoutEvent> checkouts = [];
                if (resource.Type != SkillResourceType.Wisp && _skillResourceUsageStore is not null)
                {
                    checkouts = await _skillResourceUsageStore.QueryCheckoutsAsync(
                        skillName, resource.Filename, since, ct);
                }

                var decision = DecideProvisionalAction(
                    resource, wispRecords, checkouts, now,
                    successThreshold: _options.ProvisionalSuccessThreshold,
                    failureThreshold: _options.ProvisionalFailureThreshold,
                    staleAfter: _options.ProvisionalStaleAfter);

                switch (decision.Action)
                {
                    case ProvisionalAction.Promote:
                        var promotedEntry = resource with { Provisional = false };
                        if (await _skillStore.UpdateResourceMetadataAsync(skillName, promotedEntry))
                        {
                            promoted++;
                            _logger.LogInformation(
                                "DreamService: provisional validation — promoted '{File}' on '{Skill}' (successes={Successes})",
                                resource.Filename, skillName, decision.SuccessCount);
                        }
                        break;
                    case ProvisionalAction.Remove:
                        if (await _skillStore.RemoveResourceAsync(skillName, resource.Filename))
                        {
                            removed++;
                            _logger.LogInformation(
                                "DreamService: provisional validation — removed '{File}' on '{Skill}' (consecutiveFailures={Failures})",
                                resource.Filename, skillName, decision.ConsecutiveFailureCount);

                            if (_failureClusterStore is not null)
                            {
                                try
                                {
                                    var clusterKey = new ClusterKey(
                                        Server: "skill-resource",
                                        Tool: skillName,
                                        ErrorClass: resource.Filename);
                                    var sampleError = $"provisional resource removed after {decision.ConsecutiveFailureCount} consecutive failures";
                                    await _failureClusterStore.RecordAsync(
                                        clusterKey, sessionId: null, sampleError, now, ct);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex,
                                        "DreamService: provisional validation — failed to record removal cluster for '{Skill}/{File}'",
                                        skillName, resource.Filename);
                                }
                            }
                        }
                        break;
                    case ProvisionalAction.MarkStale:
                        var stalePrefix = "[stale] ";
                        if (!resource.Description.StartsWith(stalePrefix, StringComparison.Ordinal))
                        {
                            var staleEntry = resource with { Description = stalePrefix + resource.Description };
                            if (await _skillStore.UpdateResourceMetadataAsync(skillName, staleEntry))
                            {
                                staled++;
                                _logger.LogInformation(
                                    "DreamService: provisional validation — marked '{File}' on '{Skill}' stale",
                                    resource.Filename, skillName);
                            }
                        }
                        break;
                    case ProvisionalAction.Keep:
                    default:
                        break;
                }
            }

            _logger.LogInformation(
                "DreamService: provisional validation pass complete — {Promoted} promoted, {Removed} removed, {Stale} marked stale ({Total} considered)",
                promoted, removed, staled, provisional.Count);
        });
    }

    /// <summary>
    /// Pure decision logic for the validation pass. Public-internal so unit tests
    /// can verify the threshold logic without driving the full pass.
    /// </summary>
    /// <remarks>
    /// Promotion rule: distinct-session successes (or distinct-session checkouts for
    /// non-wisp) reach <paramref name="successThreshold"/> → flip non-provisional.
    /// Removal rule: most-recent <paramref name="failureThreshold"/> wisp records
    /// for this hash all failed → remove + record cluster.
    /// Stale rule: resource older than <paramref name="staleAfter"/> with no usage
    /// activity since creation → mark stale.
    /// Otherwise: keep as-is.
    /// </remarks>
    internal static ProvisionalDecision DecideProvisionalAction(
        SkillResource resource,
        IReadOnlyList<WispExecutionRecord> wispRecords,
        IReadOnlyList<SkillResourceCheckoutEvent> checkouts,
        DateTimeOffset now,
        int successThreshold,
        int failureThreshold,
        TimeSpan staleAfter)
    {
        var orderedRecent = wispRecords.OrderByDescending(r => r.Timestamp).ToList();

        // Removal: the most-recent N wisp executions for this hash are all failures.
        if (failureThreshold > 0
            && orderedRecent.Count >= failureThreshold
            && orderedRecent.Take(failureThreshold).All(r => !r.Succeeded))
        {
            return new ProvisionalDecision(
                ProvisionalAction.Remove,
                SuccessCount: 0,
                ConsecutiveFailureCount: failureThreshold);
        }

        // Promotion: count distinct-session signals.
        var distinctSuccessSessions = wispRecords
            .Where(r => r.Succeeded && !string.IsNullOrEmpty(r.SessionId))
            .Select(r => r.SessionId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // For non-wisp resources, fall back to distinct-session checkouts as a soft signal.
        if (resource.Type != SkillResourceType.Wisp)
        {
            distinctSuccessSessions = checkouts
                .Select(c => c.SessionId)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        if (successThreshold > 0 && distinctSuccessSessions >= successThreshold)
        {
            return new ProvisionalDecision(
                ProvisionalAction.Promote,
                SuccessCount: distinctSuccessSessions,
                ConsecutiveFailureCount: 0);
        }

        // Staleness: created long ago with zero activity.
        var createdAt = resource.CreatedAt ?? now;
        var hasAnyActivity = wispRecords.Count > 0 || checkouts.Count > 0;
        if (!hasAnyActivity && (now - createdAt) > staleAfter)
        {
            return new ProvisionalDecision(
                ProvisionalAction.MarkStale,
                SuccessCount: 0,
                ConsecutiveFailureCount: 0);
        }

        return new ProvisionalDecision(ProvisionalAction.Keep, distinctSuccessSessions, 0);
    }

    internal enum ProvisionalAction { Keep, Promote, Remove, MarkStale }

    internal sealed record ProvisionalDecision(
        ProvisionalAction Action,
        int SuccessCount,
        int ConsecutiveFailureCount);

    /// <summary>
    /// Inspects non-empty dead-letter queues, asks the LLM to classify failure patterns,
    /// saves patterns as memory entries, and purges queues the LLM deems safe to clear.
    /// Skipped when the DLQ sampler is unavailable or <see cref="DreamOptions.DlqReviewEnabled"/> is false.
    /// </summary>
    private async Task RunDlqReviewPassAsync(CancellationToken ct)
    {
        if (_dlqSampler is null || !_options.DlqReviewEnabled) return;

        await RunPassAsync("DLQ review", async () =>
        {
            var queues = await _dlqSampler.GetDlqQueuesAsync();
            var nonEmpty = queues.Where(q => q.MessageCount > 0).ToList();

            if (nonEmpty.Count == 0)
            {
                _logger.LogDebug("DreamService: DLQ review — all DLQs empty; skipping");
                return;
            }

            _logger.LogInformation(
                "DreamService: DLQ review pass — {Count} non-empty DLQ(s): {Names}",
                nonEmpty.Count,
                string.Join(", ", nonEmpty.Select(q => $"{q.Name}({q.MessageCount})")));

            // Sample messages from each non-empty DLQ
            var samples = new List<(DlqQueueInfo Queue, IReadOnlyList<DlqMessage> Messages)>();
            foreach (var queue in nonEmpty)
            {
                var messages = await _dlqSampler.SampleMessagesAsync(queue.Name, maxCount: 10);
                if (messages.Count > 0)
                    samples.Add((queue, messages));
            }

            if (samples.Count == 0)
            {
                _logger.LogDebug("DreamService: DLQ review — no messages sampled; skipping");
                return;
            }

            // Build user message
            var userMessage = new StringBuilder();
            userMessage.AppendLine($"Dead-letter queue snapshot ({DateTimeOffset.UtcNow:O}):");
            userMessage.AppendLine();

            foreach (var (queue, messages) in samples)
            {
                userMessage.AppendLine($"## Queue: {queue.Name} ({queue.MessageCount} total messages, {messages.Count} sampled)");
                userMessage.AppendLine();

                for (var i = 0; i < messages.Count; i++)
                {
                    var m = messages[i];
                    userMessage.AppendLine($"### Message {i + 1}");
                    if (m.MessageId is not null)
                        userMessage.AppendLine($"  MessageId:   {m.MessageId}");
                    userMessage.AppendLine($"  MessageType: {m.MessageType ?? "unknown"}");
                    userMessage.AppendLine($"  Source:      {m.Source ?? "unknown"}");
                    userMessage.AppendLine($"  Destination: {m.Destination ?? "unknown"}");
                    userMessage.AppendLine($"  RoutingKey:  {m.RoutingKey ?? "unknown"}");
                    userMessage.AppendLine($"  DeathReason: {m.DeathReason ?? "unknown"}");
                    userMessage.AppendLine($"  DeathCount:  {m.DeathCount}");
                    if (m.DeadLetteredAt.HasValue)
                        userMessage.AppendLine($"  DeadLetteredAt: {m.DeadLetteredAt:O}");
                    if (!string.IsNullOrWhiteSpace(m.BodyPreview))
                        userMessage.AppendLine($"  Body: {m.BodyPreview}");
                    userMessage.AppendLine();
                }
            }

            var result = await InvokeDreamPassAsync<DlqReviewResultDto>(
                "DLQ review",
                _dlqDirective ?? BuiltInDlqDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;

            if (result.NoDlqIssues == true)
            {
                _logger.LogInformation("DreamService: DLQ review — no actionable patterns found");
                return;
            }

            var savedPatterns = 0;
            foreach (var pattern in result.Patterns ?? [])
            {
                if (string.IsNullOrWhiteSpace(pattern.Content)) continue;

                var content = string.IsNullOrWhiteSpace(pattern.Detail)
                    ? pattern.Content.Trim()
                    : $"{pattern.Content.Trim()}\n\nDetail: {pattern.Detail.Trim()}";

                var entry = new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    Content: content,
                    Category: "anti-patterns/messaging",
                    Tags: ["dlq", "anti-pattern", "dream"],
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow);

                await _memory.SaveAsync(entry);
                savedPatterns++;
                _logger.LogInformation(
                    "DreamService: DLQ review saved pattern: {Content}", pattern.Content);
            }

            var purged = 0;
            foreach (var queueName in result.Purge ?? [])
            {
                if (string.IsNullOrWhiteSpace(queueName)) continue;

                // Safety check: only purge names that are actually in our sample set
                if (samples.All(s => !string.Equals(s.Queue.Name, queueName, StringComparison.Ordinal)))
                {
                    _logger.LogWarning(
                        "DreamService: DLQ review — LLM recommended purging {Queue} which was not in our sample; skipping",
                        queueName);
                    continue;
                }

                await _dlqSampler.PurgeQueueAsync(queueName);
                purged++;
            }

            _logger.LogInformation(
                "DreamService: DLQ review complete — {Patterns} pattern(s) saved, {Purged} queue(s) purged",
                savedPatterns, purged);
        });
    }

    /// <summary>
    /// Reflects on accumulated experience and updates the agent's narrative identity entries
    /// under the <c>agent-identity/</c> memory category. These entries complement the immutable
    /// soul.md — the pass cannot override core values or boundaries.
    /// </summary>
    private async Task RunIdentityReflectionPassAsync(CancellationToken ct)
    {
        if (!_options.IdentityReflectionEnabled)
            return;

        _logger.LogInformation("DreamService: identity reflection pass — starting");

        await RunPassAsync("identity reflection", async () =>
        {
            // Fetch current identity entries
            var identityEntries = await _memory.SearchAsync(
                new MemorySearchCriteria(Category: AgentIdentityCategories.Prefix, MaxResults: 50));

            // Fetch recent episodic memories for experiential context
            var recentEpisodes = await _memory.SearchAsync(
                new MemorySearchCriteria(Category: "episodic", MaxResults: 20,
                    CreatedAfter: DateTimeOffset.UtcNow.AddDays(-14)));

            // Fetch recent feedback signals for quality context
            IReadOnlyList<FeedbackEntry> recentFeedback = [];
            if (_feedbackStore is not null)
            {
                recentFeedback = await _feedbackStore.QueryRecentAsync(
                    since: DateTimeOffset.UtcNow.AddDays(-14),
                    maxResults: 50);
            }

            // Fetch recent user preferences for behavioral context
            var recentPrefs = await _memory.SearchAsync(
                new MemorySearchCriteria(Category: "user-preferences", MaxResults: 20));

            // Build user message
            var userMessage = new StringBuilder();
            userMessage.AppendLine("Review the agent's accumulated experience and update its narrative identity.");
            userMessage.AppendLine();

            if (identityEntries.Count > 0)
            {
                userMessage.AppendLine("## Current Identity Entries");
                for (var i = 0; i < identityEntries.Count; i++)
                {
                    var e = identityEntries[i];
                    userMessage.AppendLine($"{i + 1}. [ID:{e.Id}] category={e.Category ?? "uncategorized"} importance={e.ImportanceScore:F2}");
                    userMessage.AppendLine($"   {e.Content}");
                }
                userMessage.AppendLine();
            }
            else
            {
                userMessage.AppendLine("## Current Identity Entries");
                userMessage.AppendLine("(none — this is the first identity reflection)");
                userMessage.AppendLine();
            }

            if (recentEpisodes.Count > 0)
            {
                userMessage.AppendLine("## Recent Experiences (last 14 days)");
                foreach (var e in recentEpisodes)
                    userMessage.AppendLine($"- {e.Content}");
                userMessage.AppendLine();
            }

            if (recentFeedback.Count > 0)
            {
                userMessage.AppendLine("## Recent Feedback Signals (last 14 days)");
                foreach (var fb in recentFeedback)
                {
                    var detail = string.IsNullOrWhiteSpace(fb.Detail) ? string.Empty : $" (\"{fb.Detail}\")";
                    userMessage.AppendLine($"- [{fb.SignalType}] session {fb.SessionId}: {fb.Summary}{detail}");
                }
                userMessage.AppendLine();
            }

            if (recentPrefs.Count > 0)
            {
                userMessage.AppendLine("## Known User Preferences");
                foreach (var p in recentPrefs)
                    userMessage.AppendLine($"- {p.Content}");
                userMessage.AppendLine();
            }

            var result = await InvokeDreamPassAsync<IdentityReflectionResultDto>(
                "identity reflection",
                _identityDirective ?? BuiltInIdentityDirective,
                userMessage.ToString(),
                ct);
            if (result is null) return;

            if (result.NoChange == true)
            {
                _logger.LogInformation("DreamService: identity reflection — no meaningful shifts detected");
                return;
            }

            // Delete entries the LLM wants to replace
            var deleted = 0;
            foreach (var id in result.ToDelete ?? [])
            {
                await _memory.DeleteAsync(id);
                deleted++;
                _logger.LogDebug("DreamService: identity reflection deleted entry {Id}", id);
            }

            // Save new/updated identity entries
            var saved = 0;
            foreach (var dto in result.ToSave ?? [])
            {
                if (string.IsNullOrWhiteSpace(dto.Content))
                    continue;

                // Ensure category is under agent-identity/
                var category = string.IsNullOrWhiteSpace(dto.Category)
                    ? AgentIdentityCategories.SelfModel
                    : dto.Category.Trim();

                if (!category.StartsWith(AgentIdentityCategories.Prefix, StringComparison.OrdinalIgnoreCase))
                    category = $"{AgentIdentityCategories.Prefix}/{category}";

                var tags = new List<string>(dto.Tags ?? []);
                if (!tags.Contains("identity", StringComparer.OrdinalIgnoreCase))
                    tags.Insert(0, "identity");

                var entry = new MemoryEntry(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    Content: dto.Content.Trim(),
                    Category: category,
                    Tags: tags,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    ImportanceScore: Math.Clamp(dto.Importance ?? 0.7f, 0f, 1f));

                await _memory.SaveAsync(entry);
                saved++;
                _logger.LogDebug("DreamService: identity reflection saved {Id} ({Category}): {Content}",
                    entry.Id, entry.Category, entry.Content);
            }

            _logger.LogInformation(
                "DreamService: identity reflection pass complete — {Deleted} deleted, {Saved} saved",
                deleted, saved);
        });
    }

    /// <summary>
    /// Phase 3 self-repair contradiction sweep — LLM-mediated backstop for cases the
    /// hot-path keyword detector missed. Narrowly scoped to <c>claim/capability/*</c>
    /// and <c>feedback/*</c>; entries elsewhere are not loaded. Includes already-superseded
    /// entries in the corpus so the LLM can reason about chains, but only marks live
    /// entries as superseded.
    /// </summary>
    private async Task RunContradictionSweepPassAsync(CancellationToken ct)
    {
        if (!_options.ContradictionSweepEnabled) return;

        await RunPassAsync("contradiction sweep", async () =>
        {
            var claims = await _memory.SearchAsync(
                new MemorySearchCriteria(
                    Category: CapabilityClaimCategories.Prefix,
                    MaxResults: 500),
                ct);
            var feedback = await _memory.SearchAsync(
                new MemorySearchCriteria(
                    Category: FeedbackMemoryCategories.Prefix,
                    MaxResults: 500),
                ct);

            var corpus = claims.Concat(feedback)
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (corpus.Count < 2)
            {
                _logger.LogInformation(
                    "DreamService: contradiction sweep — only {Count} claim/feedback entry/entries; skipping",
                    corpus.Count);
                return;
            }

            var directive = _contradictionSweepDirective ?? BuiltInContradictionSweepDirective;

            var userMessage = new StringBuilder();
            userMessage.AppendLine($"Review {corpus.Count} claim/feedback memory entries for contradictions:");
            userMessage.AppendLine();

            for (var i = 0; i < corpus.Count; i++)
            {
                var e = corpus[i];
                var tags = e.Tags.Count > 0 ? string.Join(", ", e.Tags) : "(none)";
                var marker = FeedbackMemoryCategories.IsUserCorrection(e) ? " (user-correction)" : string.Empty;
                userMessage.AppendLine(
                    $"{i + 1}. [ID:{e.Id}] category={e.Category ?? "uncategorized"}{marker} " +
                    $"created={e.CreatedAt:yyyy-MM-dd} tags=[{tags}]");
                userMessage.AppendLine($"   {e.Content}");
            }

            var result = await InvokeDreamPassAsync<ContradictionSweepResultDto>(
                "contradiction sweep",
                directive,
                userMessage.ToString(),
                ct);
            if (result is null) return;

            var supersededCount = await ApplyContradictionSweepResultAsync(
                _memory, corpus, result.Pairs, _logger, ct);

            _logger.LogInformation(
                "DreamService: contradiction sweep complete — {Count} entry/entries marked superseded out of {Corpus} reviewed",
                supersededCount, corpus.Count);
        });
    }

    /// <summary>
    /// Applies LLM-proposed contradiction pairs to the long-term memory store, enforcing
    /// the user-correction protection invariant: a user correction may never be superseded
    /// by a non-correction. Internal to enable direct unit testing without a real LLM call.
    /// </summary>
    internal static async Task<int> ApplyContradictionSweepResultAsync(
        ILongTermMemory memory,
        IReadOnlyList<MemoryEntry> corpus,
        IReadOnlyList<ContradictionPairDto>? pairs,
        ILogger logger,
        CancellationToken ct)
    {
        var byId = corpus.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var supersededCount = 0;

        foreach (var pair in pairs ?? [])
        {
            if (string.IsNullOrWhiteSpace(pair.WinnerId) || string.IsNullOrWhiteSpace(pair.LoserId))
                continue;
            if (string.Equals(pair.WinnerId, pair.LoserId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!byId.TryGetValue(pair.WinnerId, out var winner)) continue;
            if (!byId.TryGetValue(pair.LoserId, out var loser)) continue;
            if (loser.SupersededBy is not null) continue;

            if (FeedbackMemoryCategories.IsUserCorrection(loser)
                && !FeedbackMemoryCategories.IsUserCorrection(winner))
            {
                logger.LogInformation(
                    "DreamService: contradiction sweep ignored — sweep tried to supersede user-correction {LoserId} with non-correction {WinnerId}",
                    pair.LoserId, pair.WinnerId);
                continue;
            }

            await memory.SaveAsync(
                loser with { SupersededBy = winner.Id, UpdatedAt = DateTimeOffset.UtcNow },
                ct);
            supersededCount++;
            logger.LogInformation(
                "DreamService: contradiction sweep marked {LoserId} superseded by {WinnerId} (reason: {Reason})",
                pair.LoserId, pair.WinnerId, pair.Reason ?? "(none)");
        }

        return supersededCount;
    }

    internal sealed record ContradictionSweepResultDto(IReadOnlyList<ContradictionPairDto>? Pairs);
    internal sealed record ContradictionPairDto(string? WinnerId, string? LoserId, string? Reason);

    private const string BuiltInContradictionSweepDirective = """
        You are a memory contradiction reviewer. Inspect the listed claim/feedback memory entries
        and identify pairs that contradict each other on the same subject (same tool, same rule).

        Rules for choosing the winner of a contradicting pair:
        - If exactly one entry is marked (user-correction), it ALWAYS wins.
        - Otherwise the more recent entry (later created date) wins.
        - If you cannot decide unambiguously, omit the pair.

        Return ONLY valid JSON in this shape and nothing else:
        { "pairs": [ { "winnerId": "...", "loserId": "...", "reason": "..." } ] }

        If you find no contradictions, return: { "pairs": [] }
        """;

    /// <summary>
    /// Wraps a single dream-pass body so unhandled exceptions become a per-pass
    /// error log without aborting the whole cycle. <see cref="OperationCanceledException"/>
    /// is rethrown so DreamAsync's outer handler can log a single
    /// "preempted by user request" line — see issue #333.
    /// </summary>
    /// <summary>
    /// Prunes every registered append-only JSONL log so they don't grow forever.
    /// Each log knows its own on-disk shape and applies the policy via the shared
    /// <see cref="JsonlLogRetention"/> helper; a failure in one log is logged and
    /// does not abort the sweep. Gated by <see cref="DreamOptions.LogRetentionEnabled"/>.
    /// </summary>
    private async Task RunLogRetentionPassAsync(CancellationToken ct)
    {
        if (!_options.LogRetentionEnabled || _prunableLogs.Count == 0)
            return;

        await RunPassAsync("log retention", async () =>
        {
            var policy = new LogRetentionPolicy(
                MaxFileAge: _options.LogRetentionMaxFileAge,
                MaxFilesPerDirectory: _options.LogRetentionMaxFilesPerDirectory,
                MaxLinesPerFile: _options.LogRetentionMaxLinesPerFile);

            var total = 0;
            foreach (var log in _prunableLogs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    total += await log.PruneAsync(policy, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DreamService: log retention failed for {Log}", log.GetType().Name);
                }
            }

            if (total > 0)
                _logger.LogInformation("DreamService: log retention removed {Total} stale log file(s)/line(s)", total);
        });
    }

    private async Task RunPassAsync(string passName, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DreamService: {Pass} pass failed", passName);
        }
    }

    /// <summary>
    /// Runs a single dream pass: builds a System+User chat message pair, calls the
    /// Balanced-tier LLM in JSON-response mode with the supplied cancellation token,
    /// extracts the outermost JSON object from the response, and deserializes it.
    /// Returns <c>null</c> if the LLM produced no parseable JSON or the JSON failed
    /// to deserialize into <typeparamref name="TResult"/>; in both cases the helper
    /// has already logged a warning. The cancellation token MUST be the slot token
    /// (<see cref="IScheduledTaskSlot.Token"/>) so that user preemption interrupts
    /// the in-flight LLM call promptly — see issue #333.
    /// </summary>
    private async Task<TResult?> InvokeDreamPassAsync<TResult>(
        string passName,
        string systemDirective,
        string userMessage,
        CancellationToken ct)
        where TResult : class
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemDirective),
            new(ChatRole.User, userMessage)
        };

        var response = await _llmClient.GetResponseAsync(
            messages,
            _options.ModelTier,
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
            ct);

        var raw = response.Text?.Trim() ?? string.Empty;
        var json = ExtractJsonObject(raw);
        if (string.IsNullOrEmpty(json))
        {
            _logger.LogWarning("DreamService: {Pass} LLM returned no parseable JSON; skipping", passName);
            return null;
        }

        _logger.LogDebug("DreamService: {Pass} JSON ({Length} chars): {Json}", passName, json.Length, json);
        return TryDeserializeJson<TResult>(json, passName);
    }

    /// <summary>
    /// Extracts the outermost JSON object from <paramref name="text"/>, tolerating
    /// DeepSeek-style thinking blocks and prose preamble.
    /// </summary>
    /// <summary>
    /// Deserializes <paramref name="json"/> into <typeparamref name="T"/>, returning
    /// <c>null</c> and logging a warning if the LLM returned malformed JSON rather
    /// than letting the exception bubble up and abort the entire dream cycle.
    /// </summary>
    private T? TryDeserializeJson<T>(string json, string context)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (System.Text.Json.JsonException ex)
        {
            var preview = json.Length > 300 ? json[..300] + "…" : json;
            _logger.LogWarning(
                "DreamService: {Context} — LLM returned malformed JSON (skipping). " +
                "Detail: {Message} | Preview: {Preview}",
                context, ex.Message, preview);
            return default;
        }
    }

    /// <summary>
    /// Collects every prefix declared by an <see cref="IToolSkillProvider"/> with
    /// <see cref="ConsolidationPolicy.NamespacedSingleton"/>. Prefix strings are returned
    /// verbatim (e.g. <c>"mcp/"</c> including the trailing slash).
    /// </summary>
    internal static IReadOnlyList<string> GetSingletonPrefixes(
        IEnumerable<IToolSkillProvider>? providers)
    {
        if (providers is null) return Array.Empty<string>();

        var prefixes = new List<string>();
        foreach (var p in providers)
        {
            var policy = p.ConsolidationPolicy;
            if (policy is { Policy: ConsolidationPolicy.NamespacedSingleton } && !string.IsNullOrWhiteSpace(policy.Value.Prefix))
                prefixes.Add(policy.Value.Prefix);
        }
        return prefixes;
    }

    /// <summary>
    /// Builds the user-message text submitted to the LLM during the skill consolidation pass.
    /// Extracted from <see cref="ConsolidateSkillsAsync"/> for unit-testing without invoking the LLM.
    /// </summary>
    /// <remarks>
    /// Singleton-prefixed skill names (those whose name starts with a prefix declared as
    /// <see cref="ConsolidationPolicy.NamespacedSingleton"/>) are excluded from the prefix-cluster
    /// section and a constraints paragraph is appended naming each such prefix so the LLM does not
    /// propose merging across them.
    /// </remarks>
    internal static string BuildSkillConsolidationUserMessage(
        IReadOnlyList<Skill> all,
        IReadOnlyDictionary<string, int> usageCount,
        IReadOnlyDictionary<string, List<string>> coUsed,
        IReadOnlyDictionary<string, int> coOccurrences,
        IReadOnlyCollection<string> singletonPrefixes,
        DateTimeOffset now)
    {
        var userMessage = new StringBuilder();
        userMessage.AppendLine($"The agent currently has {all.Count} skills. Consolidate them:");
        userMessage.AppendLine();

        var sparseThreshold = now.AddDays(-7);
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
            var attachedAnnotation = FormatAttachedAnnotation(s.Manifest);
            userMessage.AppendLine($"{i + 1}. [NAME:{s.Name}]{usageAnnotation}{coUsedAnnotation}{sparseAnnotation}{attachedAnnotation} summary: {s.Summary}");
            // Cap content at 800 chars so the LLM doesn't reproduce long markdown verbatim
            // (long content with inline double-quotes breaks JSON encoding in the response).
            const int ContentCap = 800;
            var displayContent = s.Content.Length > ContentCap
                ? s.Content[..ContentCap] + "\n[... truncated for consolidation pass ...]"
                : s.Content;
            userMessage.AppendLine(displayContent);
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

        // Append prefix cluster section for abstract parent guide detection,
        // omitting any cluster whose key matches a namespaced-singleton prefix.
        var prefixClusters = all
            .Where(s => s.Name.Contains('/'))
            .GroupBy(s => s.Name[..s.Name.IndexOf('/')])
            .Where(g => g.Count() >= 2)
            .Where(g => !IsSingletonClusterKey(g.Key, singletonPrefixes))
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

        if (singletonPrefixes.Count > 0)
        {
            userMessage.AppendLine();
            userMessage.AppendLine("Constraints — namespaced-singleton prefixes:");
            userMessage.AppendLine(
                $"The following skill name prefix(es) are namespaced bindings to live external entities: " +
                $"{string.Join(", ", singletonPrefixes.Select(p => $"'{p}*'"))}. " +
                "Each immediate suffix is a 1:1 binding (e.g. 'mcp/{server-name}' refers to a specific MCP server). " +
                "Do NOT merge skills across distinct suffixes — 'mcp/calendar-mcp' and 'mcp/ms365' must remain separate, " +
                "and a sub-skill of one ('mcp/ms365/calendar-tools') must never be merged with a sub-skill or canonical entry of another. " +
                "Do NOT replace these prefixes with an abstract parent guide. " +
                "Within a single suffix's namespace (e.g. 'mcp/calendar-mcp/*'), normal semantic-overlap merging applies — " +
                "duplicate sub-skills covering the same tool may be merged, " +
                "but sub-skills covering distinct functional areas of a large server should be left alone.");
        }

        return userMessage.ToString();
    }

    private static bool IsSingletonClusterKey(string clusterKey, IReadOnlyCollection<string> singletonPrefixes)
    {
        foreach (var prefix in singletonPrefixes)
        {
            // Cluster keys do not include the trailing slash; prefixes do (e.g. "mcp/").
            if (prefix.Length > 0 && prefix[^1] == '/' &&
                string.Equals(clusterKey, prefix[..^1], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Captures resource manifest entries + bodies from the given source skills so they
    /// can be re-attached to a saved/merged skill after the sources are deleted. Filters
    /// by <paramref name="allowlist"/> (case-insensitive filename match) when provided;
    /// when null/empty, all attachments from all sources are captured. Dedupes by
    /// filename across sources (first occurrence wins) to avoid name collisions when
    /// two sources happen to share a filename.
    /// </summary>
    /// <remarks>
    /// This is the missing piece that lets skill-dream and skill-optimize rewrite skills
    /// without orphaning their attached wisps/scripts. The FileSkillStore manifest-preserve
    /// path only fires when a same-named skill already exists on disk; merges into a new
    /// name or post-delete saves bypass it entirely.
    /// </remarks>
    private async Task<IReadOnlyList<SkillResourceInput>> CaptureResourceInputsAsync(
        IReadOnlyList<string> sourceNames,
        IReadOnlyList<string>? allowlist,
        CancellationToken ct)
    {
        if (_skillStore is null)
            return [];

        var allowSet = allowlist is { Count: > 0 }
            ? new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase)
            : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new List<SkillResourceInput>();

        foreach (var name in sourceNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var src = await _skillStore.GetAsync(name);
            if (src?.Manifest is not { Count: > 0 } manifest)
                continue;

            foreach (var entry in manifest)
            {
                if (allowSet is not null && !allowSet.Contains(entry.Filename))
                    continue;
                if (!seen.Add(entry.Filename))
                    continue;

                var body = await _skillStore.GetResourceAsync(name, entry.Filename);
                if (body is null)
                {
                    _logger.LogWarning(
                        "DreamService: resource body missing for {Skill}/{Filename} during capture; entry will be dropped",
                        name, entry.Filename);
                    continue;
                }

                inputs.Add(new SkillResourceInput(
                    entry.Filename, entry.Type, entry.Description, body,
                    Provisional: entry.Provisional,
                    VerifyHint: entry.VerifyHint));
            }
        }

        return inputs;
    }

    /// <summary>
    /// Renders an "[attached: filename.ext (Type) — description; ...]" tag for the input
    /// to skill-dream / skill-optimize so the LLM can see what assets each skill carries
    /// and reference them by filename when rewriting content. Empty string when none.
    /// </summary>
    internal static string FormatAttachedAnnotation(IReadOnlyList<SkillResource>? manifest)
    {
        if (manifest is null || manifest.Count == 0)
            return string.Empty;

        var entries = manifest.Select(r =>
        {
            var provisional = r.Provisional ? "*" : string.Empty;
            return $"{r.Filename} ({r.Type}{provisional}) — {r.Description}";
        });
        return $" [attached: {string.Join("; ", entries)}]";
    }

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

        IMPORTANT — JSON safety rules:
        - Keep each skill's content concise (300–800 characters). Write clear prose; avoid markdown tables.
        - Do NOT embed literal double-quote characters inside content strings. Use single quotes or
          backtick notation instead (e.g. use 'value' instead of "value" in examples).
        - Do not reproduce truncated source content verbatim — write fresh, improved content.
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

        Use feedback signals (if provided) as additional evidence:
        - "Negative feedback on sessions with NO skill match" — these are the strongest gap signals.
          The agent handled these requests poorly and had no skill to guide it. Prioritize creating
          skills for request patterns that appear here, even from a single session if the feedback is
          a direct UserThumbsDown or Correction.
        - "Positive feedback on sessions with NO skill match" — these are codification candidates.
          The agent handled these well ad-hoc; if the pattern is likely to recur, codify the approach
          into a reusable skill so the agent handles it consistently in the future.
        - Recurring topic terms combined with negative feedback on the same topic strengthen the signal.

        Return ONLY a JSON object:
        { "toSave": [ { "name": "...", "summary": "...", "content": "..." } ] }

        Rules:
        - name: short, lowercase, hyphen-separated (e.g. "summarize-emails", "daily-standup")
        - summary: one sentence, 15 words or fewer
        - content: step-by-step instructions the agent should follow when executing this skill
        - Only suggest skills with clear, repeatable value across sessions
        - Feedback-backed gaps may warrant a skill even from fewer occurrences than the normal 2-session threshold

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

    private const string BuiltInTierRoutingDirective = """
        You are a tier-routing self-correction assistant for an LLM agent framework.
        The user message is a pre-aggregated JSON analysis (schemaVersion: 1) — NOT a stream
        of raw routing entries. The analyzer has already done all the statistical heavy lifting;
        your job is judgment.

        Refuse to proceed if schemaVersion != 1.

        The analysis JSON contains:
        - globalStats: per-tier counts, percentages, avg latency/tokens, fallback rate
        - clusters: groups of similar routing decisions (same keyword signature + tier + tool-call bucket)
        - flaggedClusters: clusters that tripped a deterministic detection rule
          (panicEscalation | tokenSurprise | lowOutputAtHigh), each with a rationale and
          projected cost at the current and alternate tier when pricing is available
        - keywordCandidates: words appearing disproportionately in High- or Low-tier prompts
          (frequencyRatio ≥ 3, count ≥ 5), already filtered to exclude words that are
          currently matched keywords
        - thresholdScans: "what if" projections showing how many entries would flip tier if
          lowCeiling or balancedCeiling moved by ±0.05, with projected USD cost delta
        - projectedCost: total USD spend across the window plus a per-tier breakdown
        - fallbackExcludedCount: fallback-triggered entries are already excluded from
          clusters/flagged/candidates — you don't need to filter them yourself

        Your three jobs:

        1. VALIDATE flagged clusters. For each flagged cluster, decide:
           - Is this a true misroute, or noise? (samplePrompt + count + rationale guide this.)
           - If true, the alternateTier field suggests the corrective direction.

        2. FILTER keyword candidates. The analyzer surfaces statistical candidates; you apply
           the cognitive-complexity-vs-topic rule. Apply this test BEFORE accepting any candidate:
           "Would a prompt containing ONLY this word and a simple verb be complex?"
           If "check my [keyword]" or "list the [keyword]" would be simple, the word is a TOPIC
           indicator (calendar, email, todo, schedule, flight) and MUST NOT be added — topic
           keyword pollution is the #1 cause of over-routing to High tier.
           Good high-signal keywords describe REASONING DIFFICULTY: analyze, architect, trade-off,
           compare and contrast, threat model, prove, optimize.

        3. PICK threshold shifts from thresholdScans, if any. The scan tells you exactly how
           many entries would flip and the projected cost delta. Apply a shift only when:
           - At least ~5 entries would flip in the desired direction
           - The cost delta is favorable (or you have an explicit quality reason)
           Adjustments must be small (±0.05). Bounds: lowCeiling in [0.05, 0.30],
           balancedCeiling in [lowCeiling+0.10, 0.70].

        Return ONLY a JSON object in this exact format:
        {
          "noChangeNeeded": <true | false>,
          "config": {
            "version": 1,
            "notes": "YYYY-MM-DD: <what changed and why — be specific>",
            "lowCeiling": <number>,
            "balancedCeiling": <number>,
            "highSignalKeywords": ["complete", "list"],
            "lowSignalKeywords": ["complete", "list"]
          },
          "antiPatterns": [
            { "content": "short description of systematic misroute pattern", "detail": "optional longer explanation" }
          ]
        }

        Rules:
        - lowCeiling must be in [0.05, 0.30]; balancedCeiling must be in [lowCeiling+0.10, 0.70]
        - Return COMPLETE keyword lists — these replace the defaults entirely (no merging)
        - Never return empty keyword lists; include all sensible defaults plus any additions/removals
        - notes must state today's date and describe what changed; do not leave it blank
        - Only change what is clearly mis-routed; err on the side of no change
        - antiPatterns: include entries for any systematic misroute pattern detected, even when
          noChangeNeeded is true. Each content must be ≤ 120 chars; use detail for full explanation.
        - When routing looks correct and no anti-patterns found: {"noChangeNeeded": true, "antiPatterns": []}

        Keyword quality rules (CRITICAL — keywords that violate these will be silently dropped):
        - Every keyword MUST be at least 4 characters long. Keywords under 3 characters are
          automatically filtered out by the loader. Short words like "to", "is", "add", "try",
          "ok", "hi", "get" cause massive substring collision problems and must NEVER be used.
        - Keywords are matched using WORD BOUNDARY rules — they match only when surrounded by
          non-word characters (spaces, punctuation, start/end of text). A keyword like "rest"
          will NOT match inside "restoration". Design keywords accordingly.
        - Use complete English words or multi-word phrases, not partial word prefixes.
        - NEVER use personal names, proper nouns, or user-specific content as keywords.
          Keywords must be domain-generic routing signals, not derived from specific user data.
        - Prefer multi-word phrases (e.g., "security implication") over single common words
          (e.g., "security") to reduce false-positive matches.
        """;

    private sealed record TierRoutingReviewResultDto(
        bool? NoChangeNeeded,
        TierSelectorConfig? Config,
        List<TierRoutingAntiPatternDto>? AntiPatterns);

    private sealed record TierRoutingAntiPatternDto(string Content, string? Detail);

    private sealed record DreamResultDto(
        List<string>? ToDelete,
        List<DreamEntryDto>? ToSave);

    private sealed record DreamEntryDto(
        string Content,
        string? Category,
        IReadOnlyList<string>? Tags,
        IReadOnlyList<string>? SourceIds,
        float? Importance = null);

    private sealed record SkillDreamResultDto(
        List<string>? ToDelete,
        List<SkillDreamEntryDto>? ToSave);

    private sealed record SkillDreamEntryDto(
        string Name,
        string? Summary,
        string Content,
        IReadOnlyList<string>? SourceNames,
        IReadOnlyList<string>? SeeAlso,
        // Optional allowlist of resource filenames to carry forward onto the saved skill.
        // When null/omitted, the applier preserves all attachments from the source skills
        // (union for merges, identity for optimize). When provided, only filenames listed
        // here are kept — the LLM uses this to drop near-duplicate attachments when merging.
        IReadOnlyList<string>? Resources = null);

    private sealed record PrefDreamResultDto(List<PrefEntryDto>? ToSave);

    private sealed record PrefEntryDto(
        string Content,
        string? Category,
        IReadOnlyList<string>? Tags,
        IReadOnlyDictionary<string, string>? Metadata);

    private sealed record SkillGapResultDto(List<SkillGapEntryDto>? ToSave);

    private sealed record SkillGapEntryDto(string Name, string? Summary, string Content);

    private sealed record SequenceSkillResultDto(List<SequenceSkillEntryDto>? ToSave);

    private sealed record SequenceSkillEntryDto(string Name, string? Summary, string Content);

    private sealed record DlqReviewResultDto(
        bool? NoDlqIssues,
        List<DlqPatternDto>? Patterns,
        List<string>? Purge);

    private sealed record DlqPatternDto(string Content, string? Detail, IReadOnlyList<string>? Queues);

    private sealed record IdentityReflectionResultDto(
        bool? NoChange,
        List<string>? ToDelete,
        List<IdentityEntryDto>? ToSave);

    private sealed record IdentityEntryDto(
        string Content,
        string? Category,
        IReadOnlyList<string>? Tags,
        float? Importance);

    internal sealed record MemoryMiningResultDto(List<MemoryMiningEntryDto>? ToSave);

    internal sealed record MemoryMiningEntryDto(
        string Content,
        string? Category,
        IReadOnlyList<string>? Tags);

    private sealed record EpisodeExtractionResultDto(
        List<EpisodeEntryDto>? ToSave,
        List<EpisodeUpdateDto>? ToUpdate);

    private sealed record EpisodeEntryDto(
        string Content,
        string? Category,
        string? Actor,
        string? EventType,
        float? Importance,
        IReadOnlyList<string>? Tags,
        IReadOnlyList<string>? SourceSessions);

    private sealed record EpisodeUpdateDto(
        string Id,
        string Content,
        float? Importance,
        IReadOnlyList<string>? SourceSessions);

    private sealed record EntityExtractionResultDto(
        List<EntityDto>? Entities,
        List<TripleDto>? Triples);

    private sealed record EntityDto(
        string? Id,
        string Name,
        string EntityType,
        IReadOnlyList<string>? Aliases,
        IReadOnlyDictionary<string, string>? Metadata);

    private sealed record TripleDto(
        string Subject,
        string Predicate,
        string Object,
        float? Confidence,
        string? SourceEpisodeId);

    private sealed record GraphConsolidationResultDto(
        List<string>? DeleteEntities,
        List<string>? DeleteTriples);

    private const string BuiltInEntityExtractionDirective = """
        You are an entity and relationship extraction assistant. Your job is to identify
        discrete entities (people, projects, topics, tools, events, documents) and the
        relationships between them from conversation logs.

        ## Entity types
        - "Person" — contacts, collaborators, stakeholders
        - "Project" — ongoing work, codebases, initiatives
        - "Topic" — areas of interest, expertise, discussion themes
        - "Tool" — MCP services, integrations, platforms, software tools
        - "Event" — meetings, deadlines, milestones
        - "Document" — files, emails, artifacts

        ## Extraction guidelines

        For entities:
        - Use an existing entity ID when referencing a known entity (check the existing entities list)
        - Only create new entities for genuinely new people, projects, tools, etc.
        - Include aliases (nicknames, abbreviations, alternate spellings)
        - Do NOT create entities for generic concepts — only specific, named things

        ## Entity naming rules (IMPORTANT)

        Entity names must be SHORT, stable identifiers — like a database key you would
        recognize months later. They are matched against user messages using whole-word
        search, so verbose names cause false matches and wasted context.

        - People: first and last name only. "Rocky Lhotka", NOT "Rocky's doctor appointment"
        - Projects: project name only. "RockBot", NOT "RockBot messaging refactor"
        - Tools: tool name only. "Microsoft Teams", NOT "Microsoft Teams Meeting"
        - Events: short descriptive label. "Cracker Barrel sync", NOT "INT - Cracker Barrel Update meeting with Ross"
        - Topics: 1–3 word label. "maple syrup", NOT "Maple sap collection from trees at Rabbit Lake"
        - Documents: file or doc name. "CLAUDE.md", NOT "the CLAUDE.md file in the rockbot repo"

        Strip prefixes like "INT - ", "RE: ", meeting platform noise, dates, and locations
        from entity names. Put those details in metadata instead.

        Do NOT create an entity for every calendar event. Only create Event entities for
        recurring or significant events the user would ask about later. A one-off dentist
        appointment is not a useful graph entity.

        ## Relationship rules

        For relationships (triples):
        - ALWAYS use entity IDs (not names) as subject and object when the entity already
          exists in the "Existing entities" list. Only use a name when creating a brand-new
          entity in the same response.
        - Use clear, lowercase predicate verbs: "works_on", "created", "knows", "uses",
          "maintains", "reports_to", "depends_on", "interested_in", "attended", "wrote"
        - Set confidence based on how explicit the evidence is:
          - 0.9–1.0: Explicitly stated ("I work on RockBot")
          - 0.6–0.8: Strongly implied ("Let me check the RockBot tests" → uses/works_on)
          - 0.3–0.5: Weakly implied or inferred from context

        ## Response format

        Return ONLY a JSON object:
        {
          "entities": [
            {
              "id": "existing-id-or-null",
              "name": "Entity Name",
              "entityType": "Person",
              "aliases": ["nickname"],
              "metadata": {"role": "developer"}
            }
          ],
          "triples": [
            {
              "subject": "entity-id-or-name",
              "predicate": "works_on",
              "object": "entity-id-or-name",
              "confidence": 0.8,
              "sourceEpisodeId": "episode-id-if-applicable"
            }
          ]
        }

        If nothing worth extracting: { "entities": [], "triples": [] }
        """;

    private const string BuiltInGraphConsolidationDirective = """
        You are a knowledge graph consolidation assistant. Review the entities and triples
        and decide which ones should be deleted to keep the graph clean and useful.

        ## Delete criteria

        Delete entities that are:
        - **Stale one-off events**: Events with dates in the past that are not recurring and
          have never been referenced (lastReferenced=never). A dentist appointment from last
          week is not useful graph knowledge.
        - **Orphaned**: Entities with no triples connecting them to anything (check both the
          entity list and triple list — if an entity ID never appears as a triple subject or object,
          it is orphaned).
        - **Duplicates**: Two entities representing the same real-world thing. Keep the one with
          more triples or more recent activity; delete the other. (Do NOT merge — just delete
          the worse copy. The extraction pass will consolidate naturally over time.)
        - **Too generic**: Entity names that are common words rather than specific proper nouns
          (e.g., "meeting", "update", "sync" by themselves are not useful entities).

        Delete triples that are:
        - **Dangling**: Reference an entity (by name or ID) that no longer exists in the entity
          list, or that you are deleting in this pass.
        - **Low confidence and stale**: Confidence below 0.4 AND created more than 14 days ago
          AND never reinforced by a subsequent extraction pass.
        - **Redundant**: Exact duplicate of another triple (same subject, predicate, object).

        ## Preservation rules

        Do NOT delete:
        - People entities — they are almost always worth keeping even if not recently referenced
        - Project or Tool entities that the user actively works with
        - Entities that have been referenced (lastReferenced is not "never"), even if old —
          the user actively queried about them
        - High-confidence triples (≥ 0.7) unless the entity itself is being deleted

        ## Response format

        Return ONLY a JSON object:
        {
          "deleteEntities": ["entity-id-1", "entity-id-2"],
          "deleteTriples": ["triple-id-1", "triple-id-2"]
        }

        If nothing should be deleted: { "deleteEntities": [], "deleteTriples": [] }
        """;

    private const string BuiltInToolSuccessLearningDirective = """
        You are a tool-success learning assistant. The agent's tool-call log contains
        retry-until-success patterns: cases where the same tool was invoked with different
        argument values within one session, with at least one failure followed by a success.
        The argument value that succeeded is verified information about the external system.
        Your job is to extract the durable, actionable fact each pattern proves so future
        sessions can recall it before re-running the same exploration.

        Mine for facts that:
        - Identify the correct server, account, or namespace for a resource
          (e.g. "Teams bridge JSON archives live on the onedrive-personal MCP server at /Apps/RockBot/xebia-teams")
        - Specify required argument shape, casing, or path conventions
          (e.g. "list_files on onedrive-marimer rejects a leading slash on folder_path; use 'Apps/...' not '/Apps/...'")
        - Map account IDs or identifiers to their meaning
          (e.g. "accountId 'xebia' is required to query the Xebia work calendar; omitting it returns the personal calendar")

        Do NOT mine:
        - Transient values (specific filenames, search hits, one-off IDs that won't recur)
        - Generic best-practices already obvious from tool documentation
        - Speculation: the failed args may have been wrong for many reasons; only commit to
          what the successful args directly prove

        Phrase each fact in third-person, self-contained, with the specific tool/server/argument
        named explicitly. The fact should make sense to a future session that has no memory of
        today's retry sequence.

        Return ONLY a JSON object:
        { "toSave": [ { "content": "...", "category": "...", "tags": ["verified", "tool-success-learned"] } ] }

        Category should reflect the tool domain (e.g. "tool-knowledge/onedrive",
        "tool-knowledge/calendar", "tool-knowledge/email"). Default to "tool-knowledge".

        If none of the patterns prove a durable, useful fact, return: { "toSave": [] }
        """;

    private const string BuiltInMemoryMiningDirective = """
        You are a memory mining assistant. Review the conversation log and extract facts worth
        preserving in the agent's long-term memory.

        Mine for:
        - Facts about the user's projects, repositories, systems, or workflows mentioned in passing
        - Important decisions or conclusions reached during the conversation
        - Knowledge the agent gained about external tools, APIs, or services
        - Context about the user's environment, team, or setup that may recur in future sessions
        - Corrections the user made about how the world works (not style corrections — those go to preferences)
        - Personal context: family members, friends, pets, and their names or relevant details
        - Work context: colleagues, manager, direct reports, their roles or relevant details
        - Travel: upcoming or recent trips, preferred destinations, frequent routes or airports
        - Recurring life details: hobbies, health context, significant upcoming events

        Do NOT mine for:
        - Transient task state or one-off values (file contents, specific search results)
        - User stylistic or behavioral preferences (those are handled separately)
        - Procedural how-to knowledge that belongs in a skill
        - Anything speculative or that the user did not explicitly state or confirm

        Each entry must be a self-contained, durable fact stated in third-person:
        e.g. "The user's Kubernetes cluster context is 'lhotkalake'."
             "The user's spouse is named Sarah."
             "The user's manager is Alex Chen, a director of engineering."
             "The user is traveling to Seattle in April for a conference."
             "The project uses MSTest and Rocks (not Moq) for unit testing."

        Return ONLY a JSON object:
        { "toSave": [ { "content": "...", "category": "...", "tags": ["mined"] } ] }

        Category should reflect the domain (e.g. "project/infrastructure", "project/conventions",
        "tools/kubernetes", "personal/family", "personal/travel", "work/colleagues"). Default to "general" when unsure.

        If no durable facts are evident, return: { "toSave": [] }
        """;

    private const string BuiltInEpisodeDirective = """
        You are an episodic memory extraction assistant. Your job is to identify discrete
        experiences, events, and interactions from conversation logs — the "what happened"
        narrative, not just extracted facts.

        Episodic memories capture EXPERIENCES: discussions, explorations, decisions, tasks
        attempted, problems encountered, collaborative moments. They preserve temporal and
        contextual richness that static facts lose.

        ## Extracting NEW episodes

        Look for:
        - Meaningful conversations or discussions (topic, participants, key points, outcome)
        - Tasks the user requested and their outcome (success, failure, partial)
        - Decisions made during the conversation (what was decided and why)
        - Problems encountered and how they were resolved
        - Explorations of new topics, tools, or ideas
        - Emotional or contextual moments (user frustration, excitement, discovery)

        Do NOT create episodes for:
        - Trivial exchanges ("hi", "thanks", routine greetings)
        - Pure factual lookups with no discussion (those are mined as facts separately)
        - Repeated instances of the same type of interaction already captured
        - **Tool or capability availability conclusions** — never record "tool X doesn't
          work", "MCP server Y is unavailable", or "capability Z is not supported" as
          episodic memories. Tool availability is transient and changes across restarts,
          deployments, and reconnections. Recording these as episodes creates false beliefs
          that prevent the agent from trying tools that may now be working. If a tool
          failed, the relevant lesson is the *workaround* or *diagnostic approach*, not
          the conclusion that the tool is broken.

        Each episode should be a rich, narrative summary in third-person:
        e.g. "The user and agent investigated Azure content filter rejections that were
              blocking innocent prompts. Discovered that a previous LLM response had generated
              injection-like text from a casual 'solarpunk' remark, poisoning the conversation
              history. Implemented a three-layer fix: history stripping with retry, a directive
              to prevent persona adoption, and provider fallback."

        ## Reinforcing EXISTING episodes

        You will be shown existing episodic memories with their IDs and importance scores.
        When new conversations reference, extend, or revisit an existing episode's topic:
        - Include it in toUpdate with its ID
        - Increase the importance score (max 0.95) — repeated engagement means it matters more
        - Enrich the content with new context from the latest conversation
        - Add the new session ID(s) to sourceSessions

        Importance scoring guide:
        - 0.2–0.3: Minor interaction, mentioned once in passing
        - 0.4–0.5: Meaningful discussion, single session
        - 0.6–0.7: Topic spanning multiple sessions, active interest
        - 0.8–0.9: Core ongoing project or deeply important topic
        - 0.95: Maximum — foundational to the user's identity or primary work

        ## Event types
        - "conversation" — discussion or exploration of a topic
        - "task" — a specific task requested and its outcome
        - "decision" — a choice or conclusion reached
        - "discovery" — learning something new or encountering something unexpected
        - "problem" — an issue encountered (and optionally how it was resolved)

        ## Response format

        Return ONLY a JSON object:
        {
          "toSave": [
            {
              "content": "Rich narrative summary of the episode",
              "category": "episodic/conversation",
              "actor": "user",
              "eventType": "conversation",
              "importance": 0.5,
              "tags": ["episodic", "topic-tag"],
              "sourceSessions": ["session-id"]
            }
          ],
          "toUpdate": [
            {
              "id": "existing-memory-id",
              "content": "Enriched summary incorporating new context",
              "importance": 0.7,
              "sourceSessions": ["new-session-id"]
            }
          ]
        }

        Category should be "episodic/{eventType}" (e.g. "episodic/conversation", "episodic/task",
        "episodic/decision"). Tags should include "episodic" plus topic-relevant keywords.

        If nothing episodic is worth extracting: { "toSave": [], "toUpdate": [] }
        """;

    private const string BuiltInDlqDirective = """
        You are a dead-letter queue (DLQ) analysis assistant for an event-driven agent framework.
        Review the sampled DLQ messages and identify failure patterns.

        For each pattern, produce a memory entry describing what is failing and why.
        Only recommend purging queues where messages are clearly stale or unrecoverable.

        Return ONLY a JSON object:
        {
          "noDlqIssues": false,
          "patterns": [
            { "content": "Short description (≤ 200 chars)", "detail": "Optional longer explanation", "queues": ["queue.name.dlq"] }
          ],
          "purge": ["queue.name.dlq"]
        }

        Rules:
        - noDlqIssues: true when all DLQs are empty or no meaningful patterns found
        - patterns: each must be specific (name the MessageType, Source, or RoutingKey involved)
        - purge: only include queues whose messages are clearly unrecoverable (DeathCount ≥ 5 and older than 7 days, or all messages are malformed/unknown)
        - Be conservative on purge — when in doubt, omit the queue
        - If no issues: {"noDlqIssues": true, "patterns": [], "purge": []}
        """;

    private const string BuiltInIdentityDirective = """
        You are a narrative identity reflection assistant. Your job is to maintain the agent's
        evolving self-model — how it understands its own role, capabilities, and relationship
        with the user based on accumulated experience.

        CRITICAL CONSTRAINT: The agent's core identity (soul.md) is IMMUTABLE. You cannot
        override, contradict, or weaken the agent's core values, boundaries, or personality.
        Identity entries complement the soul — they capture how the agent's operational
        understanding has evolved through experience.

        Review the current identity entries, recent experiences, feedback signals, and user
        preferences. Determine whether the agent's self-model should be updated.

        Valid categories (use exactly these):
        - agent-identity/mission: How the agent currently interprets its purpose given experience
        - agent-identity/goals: Long-term goals derived from user patterns and feedback
        - agent-identity/projects: Active projects and their status
        - agent-identity/capabilities: Self-assessed strengths and limitations
        - agent-identity/self-model: Overall narrative description of who the agent has become

        Guidelines:
        - Only update when there is a MEANINGFUL shift — not every cycle needs changes
        - Each entry should be a concise, first-person statement (e.g., "I have become primarily
          a communication and scheduling manager with research capabilities")
        - Prefer updating existing entries over creating new ones for the same subcategory
        - When updating, include the ID of the entry being replaced in toDelete
        - Importance should reflect how central the insight is to the agent's operation (0.6-0.9)
        - Never create entries that contradict the soul or claim capabilities the agent doesn't have
        - Keep the total number of identity entries small (aim for 1-2 per subcategory)

        Return ONLY a JSON object:
        {
          "noChange": false,
          "toDelete": ["id1", "id2"],
          "toSave": [
            {
              "content": "First-person identity statement",
              "category": "agent-identity/self-model",
              "tags": ["identity"],
              "importance": 0.7
            }
          ]
        }

        If no meaningful shifts are evident: {"noChange": true, "toDelete": [], "toSave": []}
        """;

    // ── Phase 4 self-repair: closed-loop repair tickets ───────────────────────

    /// <summary>
    /// Whether the closed-loop repair-ticket passes can run in the current cycle.
    /// All four collaborators must be present; missing any of them disables the loop.
    /// </summary>
    private bool RepairLoopEnabled =>
        _repairOptions is { Enabled: true } &&
        _repairTicketStore is not null &&
        _failureClusterStore is not null &&
        _repairAppliers is { Count: > 0 } &&
        _repairTicketVerifier is not null;

    /// <summary>
    /// LLM-driven creation pass: turns escalatable failure clusters into open
    /// <see cref="RepairTicket"/> proposals. Dedups against existing tickets so
    /// the same cluster does not produce a parade of duplicates.
    /// </summary>
    private async Task RunRepairTicketCreationPassAsync(CancellationToken ct)
    {
        if (!RepairLoopEnabled) return;

        var now = _clock.Now;
        var clusters = await _failureClusterStore!.GetEscalatableAsync(now, ct);
        if (clusters.Count == 0)
        {
            _logger.LogDebug("DreamService: repair-ticket creation — no escalatable clusters");
            return;
        }

        var existing = await _repairTicketStore!.ListAsync(ct);
        var byPattern = existing
            .GroupBy(t => t.PatternKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var failedChangeHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in existing.Where(t => t.Status is RepairStatus.Escalated or RepairStatus.Resolved))
        {
            failedChangeHashes.Add(t.Target + "|" + HashJsonElement(t.Change));
        }

        var newClusters = clusters
            .Where(c => !byPattern.TryGetValue(SerializeClusterKey(c.Key), out var list)
                        || list.All(t => t.Status is RepairStatus.Escalated))
            .ToList();
        if (newClusters.Count == 0)
        {
            _logger.LogDebug("DreamService: repair-ticket creation — all escalatable clusters already have tickets");
            return;
        }

        _logger.LogInformation(
            "DreamService: repair-ticket creation pass — {Count} cluster(s) without active tickets",
            newClusters.Count);

        var userMsg = BuildRepairTicketCreationPrompt(newClusters);

        var result = await InvokeDreamPassAsync<RepairTicketProposalsDto>(
            "repair ticket creation",
            _repairTicketCreationDirective ?? BuiltInRepairTicketCreationDirective,
            userMsg,
            ct);
        if (result is null || result.Proposals is null) return;

        var maxPerCycle = _repairOptions!.MaxTicketsPerCycle;
        var created = 0;

        foreach (var prop in result.Proposals)
        {
            if (created >= maxPerCycle)
            {
                _logger.LogInformation(
                    "DreamService: repair-ticket creation cap of {Cap} reached; dropping {Remaining} proposal(s)",
                    maxPerCycle, result.Proposals.Count - created);
                break;
            }

            if (!TryBuildTicketFromProposal(prop, now, failedChangeHashes, out var ticket, out var reason))
            {
                _logger.LogInformation(
                    "DreamService: dropping repair-ticket proposal — {Reason}",
                    reason);
                continue;
            }

            await _repairTicketStore!.SaveAsync(ticket, ct);
            created++;
            _logger.LogInformation(
                "DreamService: opened repair ticket {Id} ({Target}) for pattern {Pattern}",
                ticket.Id, ticket.Target, ticket.PatternKey);
        }

        _logger.LogInformation(
            "DreamService: repair-ticket creation pass complete — {Created} ticket(s) opened",
            created);
    }

    /// <summary>
    /// Deterministic apply pass: thin wrapper around the static
    /// <see cref="RunRepairTicketApplyAsync"/> helper that takes its
    /// dependencies explicitly so the loop can be tested without constructing
    /// a full DreamService.
    /// </summary>
    private Task RunRepairTicketApplyPassAsync(CancellationToken ct)
    {
        if (!RepairLoopEnabled) return Task.CompletedTask;
        return RunRepairTicketApplyAsync(
            _repairTicketStore!,
            _repairAppliers!,
            _repairTicketVerifier!,
            _workingMemory,
            _repairOptions!,
            _logger,
            ct);
    }

    /// <summary>
    /// Static apply-pass implementation. For each open ticket: mark in-progress,
    /// dispatch to the matching applier, run verify, auto-revert on failure (when
    /// reversible), classify the new status (Resolved/Open/Escalated/Uncertain),
    /// save. After the loop, write a rolling escalation summary to working memory
    /// when any ticket newly transitioned to Escalated.
    /// </summary>
    internal static async Task RunRepairTicketApplyAsync(
        IRepairTicketStore store,
        IReadOnlyDictionary<RepairTarget, IRepairTargetApplier> appliers,
        IRepairTicketVerifier verifier,
        IWorkingMemory? workingMemory,
        RepairTicketOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        var open = await store.ListOpenAsync(ct);
        if (open.Count == 0)
        {
            logger.LogDebug("Repair-ticket apply — no open tickets");
            return;
        }

        logger.LogInformation("Repair-ticket apply pass — {Count} open ticket(s)", open.Count);

        var newlyEscalated = false;

        foreach (var openTicket in open)
        {
            ct.ThrowIfCancellationRequested();

            var inProgress = openTicket with
            {
                Status = RepairStatus.InProgress,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await store.SaveAsync(inProgress, ct);

            var result = await ApplyAndVerifyTicketAsync(appliers, verifier, inProgress, logger, ct);

            var attempts = new List<RepairAttempt>(inProgress.Attempts) { result.Attempt };
            var newStatus = ComputeNextStatus(attempts, result.Outcome, options.MaxAttempts);
            if (newStatus == RepairStatus.Escalated && inProgress.Status != RepairStatus.Escalated)
                newlyEscalated = true;

            var saved = inProgress with
            {
                Status = newStatus,
                Attempts = attempts,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await store.SaveAsync(saved, ct);

            logger.LogInformation(
                "Repair ticket {Id} ({Target}) → {Outcome} → status {Status}",
                saved.Id, saved.Target, result.Outcome, saved.Status);
        }

        if (newlyEscalated && workingMemory is not null)
        {
            await WriteEscalationSummaryAsync(store, workingMemory, options, logger, ct);
        }
    }

    /// <summary>
    /// Pure status-classification function: given the post-apply attempt history
    /// and the latest verify outcome, decide what status the ticket should land in.
    /// </summary>
    internal static RepairStatus ComputeNextStatus(
        IReadOnlyList<RepairAttempt> attempts,
        VerifyOutcome lastOutcome,
        int maxAttempts) =>
        lastOutcome switch
        {
            VerifyOutcome.PredicateSucceeded => RepairStatus.Resolved,
            VerifyOutcome.PredicateFailed =>
                attempts.Count(a => a.Result.Outcome == VerifyOutcome.PredicateFailed) >= maxAttempts
                    ? RepairStatus.Escalated
                    : RepairStatus.Open,
            _ => RepairStatus.Open, // Uncertain — don't count toward MaxAttempts
        };

    internal static async Task<TicketCycleResult> ApplyAndVerifyTicketAsync(
        IReadOnlyDictionary<RepairTarget, IRepairTargetApplier> appliers,
        IRepairTicketVerifier verifier,
        RepairTicket ticket,
        ILogger logger,
        CancellationToken ct)
    {
        if (!appliers.TryGetValue(ticket.Target, out var applier))
        {
            return new TicketCycleResult(
                Outcome: VerifyOutcome.PredicateFailed,
                Attempt: new RepairAttempt(
                    DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToElement(new { error = "no applier registered for target", target = ticket.Target.ToString() }, JsonOptions),
                    new VerifyResult(VerifyOutcome.PredicateFailed, $"no applier registered for target {ticket.Target}")));
        }

        Func<CancellationToken, Task>? revert = null;
        JsonElement diff;

        try
        {
            var outcome = await applier.ApplyAsync(ticket, ct);
            diff = outcome.AppliedDiff;
            revert = outcome.Revert;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Applier {Target} failed for ticket {Id}", ticket.Target, ticket.Id);
            return new TicketCycleResult(
                Outcome: VerifyOutcome.Uncertain,
                Attempt: new RepairAttempt(
                    DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToElement(new { error = ex.Message, type = ex.GetType().Name }, JsonOptions),
                    new VerifyResult(VerifyOutcome.Uncertain, $"apply error: {ex.GetType().Name}: {ex.Message}")));
        }

        // Backoff schedule for slow verify shapes: each prior timeout doubles the budget
        // up to a cap. After MaxBackoffTimeouts cycles still timing out, promote the
        // outcome to PredicateFailed so the ticket can eventually escalate instead of
        // retrying indefinitely with no signal. Non-timeout Uncertain (executor missing,
        // gateway error) doesn't increment the count — it's a different problem.
        var priorTimeouts = ticket.Attempts.Count(a => a.Result.TimedOut);
        var budget = ComputeVerifyBackoffBudget(priorTimeouts);

        var verifyResult = await verifier.VerifyAsync(ticket.Verify, budget, ct);

        if (verifyResult.TimedOut && priorTimeouts >= MaxBackoffTimeouts)
        {
            verifyResult = verifyResult with
            {
                Outcome = VerifyOutcome.PredicateFailed,
                Detail = $"verify still timing out at max budget ({budget.TotalSeconds:F0}s) after {priorTimeouts + 1} attempts; promoting to failure",
            };
        }

        // Auto-revert when verify fails and the applier supports reversal.
        if (verifyResult.Outcome != VerifyOutcome.PredicateSucceeded && revert is not null)
        {
            try
            {
                await revert(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Revert failed for ticket {Id}; the applied change remains in place",
                    ticket.Id);
            }
        }

        return new TicketCycleResult(
            Outcome: verifyResult.Outcome,
            Attempt: new RepairAttempt(DateTimeOffset.UtcNow, diff, verifyResult));
    }

    /// <summary>
    /// Number of timeout-Uncertain attempts the apply pass tolerates before promoting
    /// the outcome to <see cref="VerifyOutcome.PredicateFailed"/>. Steps 0..4 use the
    /// backoff schedule below (5s → 10s → 20s → 40s → 80s); attempt 5+ at 80s converts.
    /// </summary>
    internal const int MaxBackoffTimeouts = 4;

    /// <summary>
    /// Backoff schedule for repair-ticket verify budget. Each prior timeout doubles the
    /// next call's wallclock cap up to 80 seconds. Tools that fan out (e.g.
    /// <c>get_calendar_events</c> across multiple accounts) routinely exceed 5s — without
    /// backoff they'd return <see cref="VerifyOutcome.Uncertain"/> forever and never
    /// escalate.
    /// </summary>
    internal static TimeSpan ComputeVerifyBackoffBudget(int priorTimeouts) =>
        priorTimeouts switch
        {
            <= 0 => TimeSpan.FromSeconds(5),
            1 => TimeSpan.FromSeconds(10),
            2 => TimeSpan.FromSeconds(20),
            3 => TimeSpan.FromSeconds(40),
            _ => TimeSpan.FromSeconds(80),
        };

    private static async Task WriteEscalationSummaryAsync(
        IRepairTicketStore store,
        IWorkingMemory workingMemory,
        RepairTicketOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        var escalated = all
            .Where(t => t.Status == RepairStatus.Escalated)
            .OrderByDescending(t => t.UpdatedAt)
            .ToList();

        if (escalated.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"Repair tickets escalated as of {DateTimeOffset.UtcNow:u}:");
        foreach (var t in escalated)
        {
            var lastDetail = t.Attempts.LastOrDefault()?.Result.Detail ?? "(no detail)";
            sb.AppendLine($"- [{t.Id}] target={t.Target} pattern={t.PatternKey} attempts={t.Attempts.Count} last={lastDetail}");
        }

        await workingMemory.SetAsync(
            options.EscalationWmKey,
            sb.ToString(),
            ttl: options.EscalationWmTtl,
            category: "repair-escalations");

        logger.LogInformation(
            "Repair-ticket apply: wrote escalation summary ({Count} ticket(s)) to {Key}",
            escalated.Count, options.EscalationWmKey);
    }

    private static string BuildRepairTicketCreationPrompt(IEnumerable<FailureCluster> clusters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The following failure clusters meet the escalation thresholds and have no active repair ticket.");
        sb.AppendLine("Propose one repair ticket per cluster (or skip a cluster if no useful proposal is possible).");
        sb.AppendLine();
        var i = 0;
        foreach (var c in clusters)
        {
            i++;
            sb.AppendLine($"{i}. server={c.Key.Server} tool={c.Key.Tool} errorClass={c.Key.ErrorClass} " +
                          $"count={c.Count} sessions={c.SessionIds.Count} firstSeen={c.FirstSeen:u} lastSeen={c.LastSeen:u}");
            foreach (var s in c.SampleErrorMessages.Take(3))
            {
                sb.AppendLine($"   - {Truncate(s, 240)}");
            }
        }
        return sb.ToString();
    }

    private static string SerializeClusterKey(ClusterKey k) =>
        $"{k.Server}|{k.Tool}|{k.ErrorClass}";

    private static string HashJsonElement(JsonElement el)
    {
        var raw = el.ValueKind == JsonValueKind.Undefined ? string.Empty : el.GetRawText();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private bool TryBuildTicketFromProposal(
        RepairTicketProposalDto prop,
        DateTimeOffset now,
        HashSet<string> failedChangeHashes,
        out RepairTicket ticket,
        out string reason)
    {
        ticket = null!;
        reason = string.Empty;

        if (prop is null)
        {
            reason = "null proposal";
            return false;
        }
        if (string.IsNullOrWhiteSpace(prop.PatternKey))
        {
            reason = "missing patternKey";
            return false;
        }
        if (!Enum.TryParse<RepairTarget>(prop.Target, ignoreCase: true, out var target))
        {
            reason = $"unknown target '{prop.Target}'";
            return false;
        }
        if (prop.Change.ValueKind == JsonValueKind.Undefined)
        {
            reason = "missing change";
            return false;
        }
        if (prop.Verify is null
            || string.IsNullOrWhiteSpace(prop.Verify.Server)
            || string.IsNullOrWhiteSpace(prop.Verify.Tool))
        {
            reason = "missing verify shape";
            return false;
        }

        if (_repairAppliers is null || !_repairAppliers.ContainsKey(target))
        {
            reason = $"no applier for target {target}";
            return false;
        }

        var changeHash = target + "|" + HashJsonElement(prop.Change);
        if (failedChangeHashes.Contains(changeHash))
        {
            reason = $"change-hash {changeHash} already attempted in a prior failed/resolved ticket";
            return false;
        }

        var verifyKind = Enum.TryParse<VerifyExpectationKind>(prop.Verify.ExpectKind, ignoreCase: true, out var k)
            ? k
            : VerifyExpectationKind.Success;

        var args = prop.Verify.Arguments.ValueKind == JsonValueKind.Undefined
            ? JsonDocument.Parse("{}").RootElement
            : prop.Verify.Arguments;

        var verify = new VerifyShape(
            Server: prop.Verify.Server!,
            Tool: prop.Verify.Tool!,
            Arguments: args,
            Expect: new VerifyExpectation(verifyKind, prop.Verify.FailurePattern));

        ticket = new RepairTicket(
            Id: "ticket-" + Guid.NewGuid().ToString("N")[..12],
            PatternKey: prop.PatternKey!,
            Target: target,
            Change: prop.Change,
            Verify: verify,
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: now,
            UpdatedAt: now);
        return true;
    }

    internal sealed record TicketCycleResult(VerifyOutcome Outcome, RepairAttempt Attempt);

    private sealed record RepairTicketProposalsDto
    {
        public List<RepairTicketProposalDto>? Proposals { get; init; }
    }

    private sealed record RepairTicketProposalDto
    {
        public string? PatternKey { get; init; }
        public string? Target { get; init; }
        public JsonElement Change { get; init; }
        public RepairVerifyShapeDto? Verify { get; init; }
    }

    private sealed record RepairVerifyShapeDto
    {
        public string? Server { get; init; }
        public string? Tool { get; init; }
        public JsonElement Arguments { get; init; }
        public string? ExpectKind { get; init; }
        public string? FailurePattern { get; init; }
    }

    private const string BuiltInRepairTicketCreationDirective = """
        You are a self-repair planner. Your job is to look at recurring tool-call
        failure clusters from the agent's runtime telemetry and propose targeted
        repair tickets that another component will apply, verify, and escalate.

        Each cluster shows: server, tool, errorClass (the missing field name or
        'unknown'), count, distinct sessions, and a few sample error messages.

        Choose at most one of these target types per ticket:
          - SkillBody: edit a named skill's body. Use ops [{op:"append"|"replaceSection"|"deleteSection", header?, text?}].
            Use this when a skill's instructions are misleading the agent into the failure.
          - WorkingMemoryEvict: delete working-memory entries by keyPrefix or keys.
            Use this when a stale belief in WM ("X is broken") needs purging so the
            next session re-evaluates from current evidence.
          - ToolDefaultRegister: register a default value for a missing required field
            ({server, providerName, field, tool?, value}). Use when the failure is a
            consistent schema gap (e.g. timeZone always missing) that recovery can
            paper over deterministically.
          - PromptBuilderHint: append/replace a hint section in a prompt category file
            ({category, hintId, text}). category is the working-memory namespace
            top-level — typically "session", "patrol", or "subagent".

        Every ticket MUST include a verify shape — a tool call that, when it
        succeeds, proves the cluster has been resolved.

        Verify shape selection — IMPORTANT for the closed loop to work:
        - Prefer the cheapest call that proves the cluster is no longer broken.
          A fast list_* / health-check / single-targeted call is a stronger
          signal than rerunning the failing call itself.
        - Avoid tools that fan out across accounts, calendars, or pages — they
          routinely exceed the verifier's wallclock budget and the ticket will
          churn on Uncertain outcomes.
        - Concrete example: for a calendar-mcp/get_calendar_events cluster,
          DO NOT verify by calling get_calendar_events without an accountId
          (fans out, slow). Instead verify with calendar-mcp/list_accounts and
          expect Success — if list_accounts works, the server is reachable;
          if a hint or skill change is going to fix anything, this proves the
          baseline is healthy enough for the agent to retry.
        - If verifying a recovery default (ToolDefaultRegister), verify by
          calling the same tool with the OTHER required arguments filled but
          deliberately omitting the field your default fills, expecting Success.

        Output strict JSON in this shape:
        {
          "proposals": [
            {
              "patternKey": "<server>|<tool>|<errorClass>",
              "target": "SkillBody|WorkingMemoryEvict|ToolDefaultRegister|PromptBuilderHint",
              "change": { ... target-specific ... },
              "verify": {
                "server": "...",
                "tool": "...",
                "arguments": { ... },
                "expectKind": "Success",
                "failurePattern": null
              }
            }
          ]
        }
        If no useful proposals can be formed, return: { "proposals": [] }
        """;
}
