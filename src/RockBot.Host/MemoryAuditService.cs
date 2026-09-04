using System.Text.Json;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Measures the long-term memory store on its own schedule and reports what it finds.
/// </summary>
/// <remarks>
/// <para>
/// Read-only by construction: it walks the memory files itself rather than holding
/// <see cref="ILongTermMemory"/> for anything but the optional duplicate-cluster probe, and its
/// own files live in a separate directory. The one thing it can write outside that directory is
/// the consolidation pause marker, and only when explicitly opted in.
/// </para>
/// <para>
/// It exists because the store's health has never been observable from inside the agent. Loki
/// keeps about a week, the dream-pass ledger records only last-run timestamps, and every
/// incident so far was found by a human pulling the PVC. A daily measurement into a file with
/// its own long retention turns "did we lose memories in August?" from forensics into a
/// question with an answer on disk.
/// </para>
/// </remarks>
internal sealed class MemoryAuditService : IHostedService, IDisposable, IPrunableLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new(JsonOptions)
    {
        WriteIndented = true
    };

    private readonly ILongTermMemory _memory;
    private readonly IAgentWorkSerializer _workSerializer;
    private readonly AgentClock _clock;
    private readonly MemoryAuditOptions _options;
    private readonly DreamOptions _dreamOptions;
    private readonly MemoryOptions _memoryOptions;
    private readonly AgentProfileOptions _profileOptions;
    private readonly ILogger<MemoryAuditService> _logger;
    private readonly ILlmClient? _llmClient;
    private readonly IMessagePublisher? _publisher;
    private readonly AgentIdentity? _agent;

    private readonly CancellationTokenSource _stopping = new();
    private readonly object _runLock = new();
    private Task _runTask = Task.CompletedTask;

    private Timer? _timer;
    private DateTimeOffset _processStartedAt;
    private CronExpression? _auditCron;
    private CronExpression? _evalCron;
    private CronExpression? _digestCron;

    public MemoryAuditService(
        ILongTermMemory memory,
        IAgentWorkSerializer workSerializer,
        AgentClock clock,
        IOptions<MemoryAuditOptions> options,
        IOptions<DreamOptions> dreamOptions,
        IOptions<MemoryOptions> memoryOptions,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<MemoryAuditService> logger,
        ILlmClient? llmClient = null,
        IMessagePublisher? publisher = null,
        AgentIdentity? agent = null)
    {
        _memory = memory;
        _workSerializer = workSerializer;
        _clock = clock;
        _options = options.Value;
        _dreamOptions = dreamOptions.Value;
        _memoryOptions = memoryOptions.Value;
        _profileOptions = profileOptions.Value;
        _logger = logger;
        _llmClient = llmClient;
        _publisher = publisher;
        _agent = agent;
    }

    /// <summary>Directory holding the audit's own files.</summary>
    internal string AuditRoot => ResolveUnderProfile(_options.BasePath);

    /// <summary>The memory store's root, resolved exactly as <see cref="FileMemoryStore"/> resolves it.</summary>
    internal string MemoryRoot =>
        FileMemoryStore.ResolvePath(_memoryOptions.BasePath, _profileOptions.BasePath);

    private string SnapshotsPath => Path.Combine(AuditRoot, MemoryAuditFiles.SnapshotsFile);
    private string StatePath => Path.Combine(AuditRoot, MemoryAuditFiles.StateFile);
    private string LatestReportPath => Path.Combine(AuditRoot, MemoryAuditFiles.LatestReport);
    private string EvalLatestPath => Path.Combine(AuditRoot, MemoryAuditFiles.EvalLatest);
    private string PausedPath => Path.Combine(AuditRoot, MemoryAuditFiles.ConsolidationPausedFile);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("MemoryAuditService: disabled; skipping timer setup");
            return Task.CompletedTask;
        }

        try
        {
            _auditCron = ParseCron(_options.CronSchedule);
            _evalCron = _options.EvalEnabled ? ParseCron(_options.EvalCronSchedule) : null;
            _digestCron = string.IsNullOrWhiteSpace(_options.DigestCronSchedule)
                ? null
                : ParseCron(_options.DigestCronSchedule!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "MemoryAuditService: invalid cron expression; the memory audit is disabled");
            return Task.CompletedTask;
        }

        // Held in memory and folded into the state at audit time rather than written here.
        // Writing a state file at start-up would make the very first audit compare the corpus
        // against an empty one and report every existing entry as newly created.
        _processStartedAt = _clock.Now;

        _timer = new Timer(
            _ =>
            {
                lock (_runLock)
                    _runTask = OnTimerTickAsync();
            },
            null,
            _options.InitialDelay,
            Timeout.InfiniteTimeSpan);

        _logger.LogInformation(
            "MemoryAuditService: scheduled — first run in {InitialDelay}, then on cron '{Cron}' " +
            "(eval '{EvalCron}'), reading {MemoryRoot}, writing {AuditRoot}",
            _options.InitialDelay, _options.CronSchedule,
            _evalCron is null ? "disabled" : _options.EvalCronSchedule,
            MemoryRoot, AuditRoot);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        _stopping.Cancel();

        Task run;
        lock (_runLock)
            run = _runTask;

        if (run.IsCompleted) return;

        try
        {
            await run.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MemoryAuditService: run did not finish within the shutdown timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MemoryAuditService: run faulted during shutdown");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();

        Task run;
        lock (_runLock)
            run = _runTask;

        if (run.IsCompleted)
            _stopping.Dispose();
    }

    // ── Scheduling ────────────────────────────────────────────────────────────

    /// <summary>
    /// Next moment either the audit or the eval is due. A single timer serves both so the
    /// service holds one callback rather than racing two against the same work slot.
    /// </summary>
    internal static DateTimeOffset? ComputeNextDue(
        DateTimeOffset now,
        TimeZoneInfo zone,
        CronExpression audit,
        CronExpression? eval)
    {
        var nextAudit = audit.GetNextOccurrence(now, zone);
        var nextEval = eval?.GetNextOccurrence(now, zone);

        if (nextAudit is null) return nextEval;
        if (nextEval is null) return nextAudit;
        return nextAudit <= nextEval ? nextAudit : nextEval;
    }

    private async Task OnTimerTickAsync()
    {
        try
        {
            var now = _clock.Now;

            // The eval reads content and costs LLM calls; the audit is model-free. Both are due
            // on the same tick whenever their cron slots coincide, and the audit runs first so
            // the eval's numbers land in a snapshot that already exists.
            var evalDue = _evalCron is not null && IsDue(_evalCron, now);
            var auditDue = _auditCron is not null && IsDue(_auditCron, now);

            // A tick that matches neither (the very first one, armed from InitialDelay) still
            // runs the audit — a fresh pod should produce a baseline without waiting for 04:00.
            if (auditDue || !evalDue)
                await RunAuditAsync(_stopping.Token);

            if (evalDue)
                await RunEvalAsync(_stopping.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MemoryAuditService: run cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MemoryAuditService: timer tick failed");
        }
        finally
        {
            ArmNextTimer();
        }
    }

    /// <summary>
    /// Whether <paramref name="cron"/> has an occurrence within a minute of now. The timer is
    /// armed on the occurrence itself, so a small tolerance absorbs scheduling jitter without
    /// letting a late tick fire the wrong pass.
    /// </summary>
    private bool IsDue(CronExpression cron, DateTimeOffset now)
    {
        var occurrence = cron.GetNextOccurrence(now.AddMinutes(-1), _clock.Zone, inclusive: true);
        return occurrence is not null && occurrence.Value <= now.AddMinutes(1);
    }

    private void ArmNextTimer()
    {
        if (_auditCron is null || _stopping.IsCancellationRequested) return;

        var next = ComputeNextDue(_clock.Now, _clock.Zone, _auditCron, _evalCron);
        if (next is null)
        {
            _logger.LogWarning(
                "MemoryAuditService: cron '{Cron}' has no future occurrences; the audit has stopped",
                _options.CronSchedule);
            return;
        }

        var delay = next.Value - _clock.Now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        try
        {
            _timer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _logger.LogInformation("MemoryAuditService: next run at {Next} (in {Delay:g})", next.Value, delay);
    }

    // ── Audit run ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the store, computes a snapshot, writes the trend row and the report, and surfaces
    /// anything that needs attention. Returns the snapshot, or null when the slot was busy.
    /// </summary>
    internal async Task<MemoryAuditSnapshot?> RunAuditAsync(CancellationToken ct)
    {
        // The audit reads every memory file. Taking the scheduled slot keeps that off the same
        // moment as a dream cycle rewriting them, and lets a user message preempt it.
        IScheduledTaskSlot? slot;
        try
        {
            slot = await _workSerializer.TryAcquireForScheduledAsync(ct);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }

        if (slot is null)
        {
            _logger.LogInformation("MemoryAuditService: agent busy, deferring this run to the next slot");
            return null;
        }

        try
        {
            var token = slot.Token;
            Directory.CreateDirectory(AuditRoot);

            var now = _clock.Now;
            var snapshotId = Guid.NewGuid().ToString("N")[..12];

            var walk = await MemoryStoreWalker.WalkAsync(MemoryRoot, _logger, token);
            var previous = await MemoryAuditState.LoadAsync(StatePath, _logger, token);

            var (snapshot, state) = MemoryAuditAnalyzer.Analyze(
                walk,
                previous,
                await ReadPassLedgerAsync(),
                ProcessStartsIncludingThisOne(previous),
                await ProbeEmbeddingClustersAsync(token),
                ReadVocabularyStoplistSize(),
                await ReadEvalSummaryAsync(token),
                _dreamOptions,
                _options,
                now,
                snapshotId,
                token);

            await AppendSnapshotAsync(snapshot, token);
            await state.SaveAsync(StatePath, token);

            var trend = await ReadTrendAsync(TimeSpan.FromDays(60), token);
            var report = MemoryAuditReportWriter.Render(snapshot, trend);

            await AtomicFile.WriteAllTextAsync(LatestReportPath, report, token);
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(AuditRoot, $"report-{now:yyyy-MM-dd}.md"), report, token);

            await CopyReportToSharedAsync(report, now, token);

            // One structured line per run. This is what makes the audit visible in Loki without
            // anyone opening a file, and it carries the numbers a dashboard would chart.
            _logger.LogInformation(
                "memory audit — status {Status}, live {Live}, archived {Archived}, created {Created}, " +
                "archived-since {ArchivedSince}, hard-deleted {HardDeleted} ({OutsidePurge} outside purge), " +
                "net {NetPerDay}/day, near-dup pairs {NearDupPairs}, max chain depth {ChainDepth}, " +
                "rejected sources {Rejected}, restarts {Restarts}, invariant failures {Violations}",
                snapshot.Status, snapshot.Live, snapshot.Archived, snapshot.CreatedSinceLast,
                snapshot.ArchivedSinceLast, snapshot.HardDeletedSinceLast, snapshot.HardDeletedOutsidePurge,
                FormatRateForLog(snapshot.NetGrowthPerDay), snapshot.NearDupPairs, snapshot.MaxChainDepth,
                snapshot.RejectedMergeSourcesSinceLast, snapshot.RestartsSinceLast,
                snapshot.Invariants.Count);

            await MaybePauseConsolidationAsync(snapshot, now, token);
            await SurfaceAsync(snapshot, report, state, now, token);

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MemoryAuditService: audit preempted by other agent work");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MemoryAuditService: audit run failed");
            return null;
        }
        finally
        {
            await slot.DisposeAsync();
        }
    }

    /// <summary>
    /// The growth rate as the structured log line should carry it. A window too short to measure
    /// a rate over reads as "n/a" — the raw null renders as "(null)", which a dashboard parsing
    /// this line would have to special-case anyway.
    /// </summary>
    private static string FormatRateForLog(double? rate) =>
        rate is { } value ? value.ToString("+0.0;-0.0;0", System.Globalization.CultureInfo.InvariantCulture) : "n/a";

    // ── Eval run ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Samples recent memory-management decisions and asks a model whether they were right.
    /// Skipped entirely when the corpus has not moved since the last eval.
    /// </summary>
    internal async Task<MemoryAuditEvalResult?> RunEvalAsync(CancellationToken ct)
    {
        if (!_options.EvalEnabled || _llmClient is null) return null;

        IScheduledTaskSlot? slot;
        try
        {
            slot = await _workSerializer.TryAcquireForScheduledAsync(ct);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }

        if (slot is null)
        {
            _logger.LogInformation("MemoryAuditService: agent busy, deferring the sample eval");
            return null;
        }

        try
        {
            var token = slot.Token;
            Directory.CreateDirectory(AuditRoot);

            var now = _clock.Now;
            var walk = await MemoryStoreWalker.WalkAsync(MemoryRoot, _logger, token);
            var state = await MemoryAuditState.LoadAsync(StatePath, _logger, token);

            var fingerprint = MemoryAuditEvaluator.StoreFingerprint(walk.Entries);
            if (string.Equals(fingerprint, state?.LastEvalFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "MemoryAuditService: sample eval skipped — the corpus has not changed since {LastEval:u}",
                    state!.LastEvalAt);
                return null;
            }

            var live = walk.Entries.Where(e => e.ArchivedAt is null).ToList();
            var pairs = ShingleSimilarity.FindNearDuplicatePairs(
                live, _options.ShingleSize, _options.NearDuplicateThreshold, token);

            var samples = MemoryAuditEvaluator.SelectSamples(walk.Entries, pairs, _options, now);
            if (samples.Count == 0)
            {
                _logger.LogInformation("MemoryAuditService: sample eval found nothing to judge");
                return null;
            }

            var evaluator = new MemoryAuditEvaluator(_llmClient, _logger);
            var result = await evaluator.EvaluateAsync(
                samples, LoadEvalDirective(), _options.EvalModelTier, fingerprint, token);

            if (result is null) return null;

            var json = JsonSerializer.Serialize(result, IndentedJsonOptions);
            await AtomicFile.WriteAllTextAsync(EvalLatestPath, json, token);
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(AuditRoot, $"eval-{now:yyyy-MM-dd}.json"), json, token);

            if (state is not null)
                await (state with
                {
                    LastEvalAt = now,
                    LastEvalFingerprint = fingerprint
                }).SaveAsync(StatePath, token);

            _logger.LogInformation(
                "memory audit eval — {Sampled} sample(s) judged, {Sound} sound ({Rate:P0})",
                result.Summary.Sampled, result.Summary.Sound, result.Summary.SoundRate);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MemoryAuditService: sample eval preempted by other agent work");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MemoryAuditService: sample eval failed");
            return null;
        }
        finally
        {
            await slot.DisposeAsync();
        }
    }

    // ── Retention ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Prunes the audit's own files. Dated reports and evals follow the dream cycle's shared
    /// file-age policy; <c>snapshots.jsonl</c> does not — it honours
    /// <see cref="MemoryAuditOptions.SnapshotRetention"/> instead, because the trend is the one
    /// thing here that must outlive log retention by a wide margin.
    /// </summary>
    public async Task<int> PruneAsync(LogRetentionPolicy policy, CancellationToken ct = default)
    {
        if (!Directory.Exists(AuditRoot)) return 0;

        var removed = 0;

        removed += await JsonlLogRetention.PruneAgedFilesAsync(
            AuditRoot, policy.MaxFileAge, policy.MaxFilesPerDirectory, "report-*.md", _logger, ct);

        removed += await JsonlLogRetention.PruneAgedFilesAsync(
            AuditRoot, policy.MaxFileAge, policy.MaxFilesPerDirectory, "eval-*.json", _logger, ct);

        removed += await TrimSnapshotsAsync(ct);

        return removed;
    }

    private async Task<int> TrimSnapshotsAsync(CancellationToken ct)
    {
        if (_options.SnapshotRetention <= TimeSpan.Zero || !File.Exists(SnapshotsPath))
            return 0;

        try
        {
            var cutoff = _clock.Now - _options.SnapshotRetention;
            var lines = await File.ReadAllLinesAsync(SnapshotsPath, ct);

            // A line that will not parse is kept, not dropped. It is either a row written by a
            // future schema or a genuinely corrupt one, and neither is something a retention
            // sweep should quietly destroy.
            var kept = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => TryParseSnapshot(line) is not { } snapshot || snapshot.TakenAt >= cutoff)
                .ToList();

            var dropped = lines.Length - kept.Count;
            if (dropped <= 0) return 0;

            await AtomicFile.WriteAllTextAsync(
                SnapshotsPath, string.Join(Environment.NewLine, kept) + Environment.NewLine, ct);

            _logger.LogInformation(
                "MemoryAuditService: trimmed {Dropped} snapshot row(s) older than {Retention:g}",
                dropped, _options.SnapshotRetention);
            return dropped;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryAuditService: failed to trim {Path}", SnapshotsPath);
            return 0;
        }
    }

    // ── Surfacing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes an unsolicited message when the run needs attention, and the full report on the
    /// digest schedule. A healthy run with no digest due says nothing at all.
    /// </summary>
    private async Task SurfaceAsync(
        MemoryAuditSnapshot snapshot,
        string report,
        MemoryAuditState state,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (_publisher is null || _agent is null) return;

        var digestDue = _digestCron is not null
            && _digestCron.GetNextOccurrence(
                   state.LastDigestAt ?? now - TimeSpan.FromDays(365), _clock.Zone) is { } due
            && due <= now;

        var needsAttention = _options.AlertOnAttention
            && !string.Equals(snapshot.Status, MemoryAuditStatuses.Healthy, StringComparison.Ordinal);

        if (!needsAttention && !digestDue) return;

        var content = digestDue
            ? report
            : $"**Memory audit — {snapshot.Status}**\n\n" +
              $"{snapshot.Live} live entries, {snapshot.Archived} archived.\n\n" +
              string.Join("\n", snapshot.Invariants.Select(v => $"- **`{v.Name}`** — {v.Message}")) +
              $"\n\nFull report: `{LatestReportPath}` (or call `get_memory_audit`).";

        try
        {
            var reply = new AgentReply
            {
                Content = content,
                SessionId = WellKnownSessions.ScheduledSystem,
                AgentName = _agent.Name,
                IsFinal = true,
                Origin = new ReplyOrigin(
                    Channel: "memory-audit",
                    PromptSummary: $"memory audit — {snapshot.Status}",
                    StartedAt: now,
                    SessionId: WellKnownSessions.ScheduledSystem)
            };

            await _publisher.PublishAsync(
                $"{UserProxyTopics.UserResponse}.{_agent.Name}",
                reply.ToEnvelope<AgentReply>(source: _agent.Name),
                ct);

            if (digestDue)
                await (state with { LastDigestAt = now }).SaveAsync(StatePath, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Failing to announce a finding must not lose the finding — it is already on disk.
            _logger.LogWarning(ex, "MemoryAuditService: could not publish the audit message");
        }
    }

    /// <summary>
    /// Writes the marker that stops dream consolidation, when the run found catastrophic loss
    /// and the operator opted in. Never cleared here — resuming is a deliberate act.
    /// </summary>
    private async Task MaybePauseConsolidationAsync(
        MemoryAuditSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        if (!_options.PauseConsolidationOnAlert) return;
        if (File.Exists(PausedPath)) return;

        var lostOutsidePurge = snapshot.HardDeletedOutsidePurge > _options.MaxHardDeletesOutsidePurge;
        var lostTooMuch = snapshot.Invariants.Any(v => v.Name == MemoryAuditInvariants.LossPercentThreshold);

        if (!lostOutsidePurge && !lostTooMuch) return;

        var reason = lostOutsidePurge
            ? $"{snapshot.HardDeletedOutsidePurge} entry(s) were hard-deleted outside the retention purge"
            : $"live entries dropped more than {_options.MaxLossPercentBetweenSnapshots:F0}% since the previous snapshot";

        try
        {
            await AtomicFile.WriteAllTextAsync(
                PausedPath,
                JsonSerializer.Serialize(
                    new { reason, snapshotId = snapshot.SnapshotId, pausedAt = now },
                    IndentedJsonOptions),
                ct);

            _logger.LogError(
                "MemoryAuditService: PAUSED memory consolidation — {Reason}. Delete {Path} " +
                "(or call resume_memory_consolidation) once the cause is understood.",
                reason, PausedPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MemoryAuditService: failed to write the consolidation pause marker");
        }
    }

    private async Task CopyReportToSharedAsync(string report, DateTimeOffset now, CancellationToken ct)
    {
        if (!_options.CopyReportToShared || string.IsNullOrWhiteSpace(_options.SharedReportDirectory))
            return;

        try
        {
            // Re-created every run: the shared volume's cleanup CronJob deletes everything under
            // exports/ past its TTL, directories included.
            Directory.CreateDirectory(_options.SharedReportDirectory);

            var path = Path.Combine(_options.SharedReportDirectory, $"memory-audit-{now:yyyy-MM-dd}.md");
            await File.WriteAllTextAsync(path, report, ct);

            // World-writable, like every other file the agent puts on the shared volume: the
            // script pods and MCP servers that read it run as different uids.
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            }
            catch
            {
                // Non-Unix platforms — best-effort only.
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MemoryAuditService: could not copy the report to {Directory}",
                _options.SharedReportDirectory);
        }
    }

    // ── File helpers ──────────────────────────────────────────────────────────

    private async Task AppendSnapshotAsync(MemoryAuditSnapshot snapshot, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.AppendAllTextAsync(SnapshotsPath, line + Environment.NewLine, ct);
    }

    /// <summary>Snapshot rows written within <paramref name="window"/>, oldest first.</summary>
    internal async Task<IReadOnlyList<MemoryAuditSnapshot>> ReadTrendAsync(
        TimeSpan window, CancellationToken ct = default)
    {
        if (!File.Exists(SnapshotsPath)) return [];

        var cutoff = _clock.Now - window;
        var snapshots = new List<MemoryAuditSnapshot>();

        try
        {
            foreach (var line in await File.ReadAllLinesAsync(SnapshotsPath, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var snapshot = TryParseSnapshot(line);
                if (snapshot is not null && snapshot.TakenAt >= cutoff)
                    snapshots.Add(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryAuditService: could not read {Path}", SnapshotsPath);
        }

        return snapshots;
    }

    private static MemoryAuditSnapshot? TryParseSnapshot(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<MemoryAuditSnapshot>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<MemoryAuditEvalSummary?> ReadEvalSummaryAsync(CancellationToken ct)
    {
        if (!File.Exists(EvalLatestPath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(EvalLatestPath, ct);
            return JsonSerializer.Deserialize<MemoryAuditEvalResult>(json, JsonOptions)?.Summary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadPassLedgerAsync()
    {
        var path = ResolveUnderProfile(DreamPassLedger.FileName);
        var ledger = await DreamPassLedger.LoadAsync(path, _logger);
        return ledger.Records.ToDictionary(
            kv => kv.Key, kv => kv.Value.LastRunAt, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asks the store for its own duplicate clusters, when it can answer. Returns null rather
    /// than zero when it cannot — "the detector found nothing" and "there is no detector" are
    /// different findings.
    /// </summary>
    private async Task<int?> ProbeEmbeddingClustersAsync(CancellationToken ct)
    {
        if (_memory is not IMemoryDuplicateCandidates candidates) return null;

        try
        {
            var clusters = await candidates.FindNearDuplicateClustersAsync(
                _dreamOptions.ConsolidationSimilarityThreshold,
                _dreamOptions.ConsolidationMaxClusterSize,
                ct);
            return clusters.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryAuditService: the store's duplicate probe failed");
            return null;
        }
    }

    /// <summary>
    /// Size of the merge-coverage common-word list. Read straight from the file rather than
    /// through the loader so the audit does not emit the loader's per-load Information line.
    /// </summary>
    private int ReadVocabularyStoplistSize()
    {
        var path = ResolveUnderProfile(_dreamOptions.MergeCoverageVocabularyPath);
        if (!File.Exists(path)) return 0;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("extraCommonWords", out var words)
                   && words.ValueKind == JsonValueKind.Array
                ? words.GetArrayLength()
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private string LoadEvalDirective()
    {
        var path = ResolveUnderProfile(_options.EvalDirectivePath);

        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MemoryAuditService: could not read {Path}; using the built-in eval directive", path);
        }

        return MemoryAuditEvaluator.BuiltInDirective;
    }

    /// <summary>
    /// Carried-over start times plus this process's own, deduplicated. A pod that runs for a
    /// week appends the same timestamp on every audit, so it is counted once — on the first run
    /// after the restart, which is the run where it explains anything.
    /// </summary>
    private IReadOnlyList<DateTimeOffset> ProcessStartsIncludingThisOne(MemoryAuditState? previous)
    {
        var starts = new SortedSet<DateTimeOffset>(previous?.ProcessStarts ?? []);
        if (_processStartedAt != default)
            starts.Add(_processStartedAt);
        return [.. starts];
    }

    private string ResolveUnderProfile(string path)
    {
        if (Path.IsPathRooted(path)) return path;

        var baseDir = Path.IsPathRooted(_profileOptions.BasePath)
            ? _profileOptions.BasePath
            : Path.Combine(AppContext.BaseDirectory, _profileOptions.BasePath);

        return Path.Combine(baseDir, path);
    }

    private static CronExpression ParseCron(string expression) =>
        CronExpression.Parse(
            expression,
            expression.Split(' ').Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
}
