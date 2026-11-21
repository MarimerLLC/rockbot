using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Host;

// ── Scheduled-task efficiency (#848) ──────────────────────────────────────────
//
// Every other self-improvement pass is triggered by a failure or a frequency, so a recurring task
// that succeeds every week at a high cost — re-reading the same mailbox, re-judging the same items,
// reporting "nothing new" — never trips any of them. This pass picks such tasks from their run
// history (ScheduledTaskEfficiencyAdvisor, deterministic), shows the model what a run actually did,
// and applies what it proposes: a tighter skill, a directive with a skip-list, a saved wisp for the
// fixed fetch steps, and a gate that skips the LLM turn when that wisp finds nothing.
internal sealed partial class DreamService
{
    private readonly ISavedWispRunner? _savedWispRunner;
    private readonly ISavedResponseStore? _savedResponseStore;
    private string? _scheduledTaskEfficiencyDirective;

    /// <summary>Who switched a gate on, for <see cref="TaskGate.EnabledBy"/>.</summary>
    internal const string OptimizerGateOwner = "optimizer";

    private const int EfficiencyMaxSkillChars = 8_000;
    private const int EfficiencyMaxReplyChars = 1_500;
    private const int EfficiencyMaxToolCalls = 60;
    private const int EfficiencyMaxWispBodyChars = 2_000;
    private const int EfficiencyMaxSkillsPerTask = 3;

    private void LoadScheduledTaskEfficiencyDirective()
    {
        if (!_options.ScheduledTaskEfficiencyEnabled || _scheduledTaskStore is null || _scheduledTaskRunCosts is null)
            return;

        var path = ResolvePath(_options.ScheduledTaskEfficiencyDirectivePath, _profileOptions.BasePath);
        _scheduledTaskEfficiencyDirective = File.Exists(path) ? File.ReadAllText(path) : null;
        if (_scheduledTaskEfficiencyDirective is null)
            _logger.LogDebug("DreamService: scheduled-task efficiency directive not found at {Path}; using built-in", path);
        else
            _logger.LogDebug("DreamService: loaded scheduled-task efficiency directive from {Path}", path);
    }

    /// <summary>
    /// Reviews the recurring tasks <see cref="ScheduledTaskEfficiencyAdvisor"/> picks, costliest
    /// first, and applies the model's proposals (see <see cref="ApplyEfficiencyProposalAsync"/>).
    /// Gated on the picked tasks' run counts and last runs, so it re-runs only after new runs.
    /// </summary>
    internal async Task RunScheduledTaskEfficiencyPassAsync(CancellationToken ct)
    {
        if (!_options.ScheduledTaskEfficiencyEnabled || _scheduledTaskStore is null || _scheduledTaskRunCosts is null)
            return;

        await RunPassAsync("scheduled-task efficiency", async () =>
        {
            var thresholds = new ScheduledTaskEfficiencyAdvisor.Thresholds(
                _options.EfficiencyMinRuns, _options.EfficiencyMinMedianCostUsd, _options.EfficiencyMinToolCalls);

            var candidates = new List<ScheduledTaskEfficiencyAdvisor.Candidate>();
            foreach (var task in await _scheduledTaskStore.ListAsync())
            {
                ct.ThrowIfCancellationRequested();
                var runs = await _scheduledTaskRunCosts.GetRunsAsync(task.Name, ct);
                var optimizations = await _scheduledTaskRunCosts.GetOptimizationsAsync(task.Name, ct);
                if (ScheduledTaskEfficiencyAdvisor.Select(task, runs, optimizations.LastOrDefault(), thresholds) is { } candidate)
                    candidates.Add(candidate);
            }

            if (candidates.Count == 0)
            {
                _logger.LogDebug("DreamService: scheduled-task efficiency — no candidate tasks");
                return;
            }

            var picked = candidates
                .OrderByDescending(c => c.MedianCostUsd)
                .Take(Math.Max(1, _options.EfficiencyMaxTasksPerCycle))
                .OrderBy(c => c.Task.Name, StringComparer.Ordinal)
                .ToList();

            var userMessage = new StringBuilder();
            userMessage.AppendLine(CultureInfo.InvariantCulture, $"Current date/time: {_clock.Now:yyyy-MM-dd HH:mm zzz}");
            userMessage.AppendLine(CultureInfo.InvariantCulture, $"Tasks to review: {picked.Count}");
            userMessage.AppendLine();
            var shownSkills = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in picked)
                shownSkills[candidate.Task.Name] = await AppendEfficiencyTaskAsync(userMessage, candidate, ct);

            var result = await InvokeDreamPassAsync<ScheduledTaskEfficiencyResultDto>(
                "scheduled-task efficiency",
                _scheduledTaskEfficiencyDirective ?? BuiltInScheduledTaskEfficiencyDirective,
                userMessage.ToString(),
                ct,
                EfficiencyFingerprint(picked));
            if (result?.Tasks is not { Count: > 0 } proposals)
                return;

            var applied = 0;
            foreach (var proposal in proposals)
            {
                var candidate = picked.FirstOrDefault(c =>
                    string.Equals(c.Task.Name, proposal.Task?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (candidate is null)
                {
                    _logger.LogDebug("DreamService: scheduled-task efficiency — proposal for unknown task '{Task}'", proposal.Task);
                    continue;
                }

                if (await ApplyEfficiencyProposalAsync(candidate, proposal, shownSkills[candidate.Task.Name], ct) is not null)
                    applied++;
            }

            _logger.LogInformation(
                "DreamService: scheduled-task efficiency pass complete — {Applied} of {Picked} task(s) changed",
                applied, picked.Count);
        });
    }

    /// <summary>
    /// The change-gate fingerprint: each picked task's run count and last run. It moves only when a
    /// picked task runs again (or the set of picked tasks changes).
    /// </summary>
    internal static string EfficiencyFingerprint(IEnumerable<ScheduledTaskEfficiencyAdvisor.Candidate> candidates) =>
        CorpusFingerprint(candidates
            .OrderBy(c => c.Task.Name, StringComparer.Ordinal)
            .Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{c.Task.Name}|{c.Runs.Count}|{c.Runs[^1].EndedAt.UtcTicks}")));

    /// <summary>
    /// Writes one task's section of the prompt and returns the names of the skills it showed, the
    /// only skills a proposal for this task may rewrite.
    /// </summary>
    private async Task<HashSet<string>> AppendEfficiencyTaskAsync(
        StringBuilder sb, ScheduledTaskEfficiencyAdvisor.Candidate candidate, CancellationToken ct)
    {
        var task = candidate.Task;
        var sessionId = WellKnownSessions.ScheduledTaskPrefix + task.Name;

        sb.AppendLine(CultureInfo.InvariantCulture, $"## Task: {task.Name}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- cron: `{task.CronExpression}`{(task.TimeZone is null ? "" : $" ({task.TimeZone})")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- last fired: {(task.LastFiredAt is { } fired ? fired.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) : "never")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- median cost per run: ${candidate.MedianCostUsd:0.00}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- why it was picked: {string.Join("; ", candidate.Signals)}");
        sb.AppendLine(task.Gate is { } gate
            ? $"- current gate: wisp {gate.Target}, mode {gate.Mode}, set by {gate.EnabledBy ?? "unknown"}"
            : "- current gate: none");
        sb.AppendLine();
        sb.AppendLine("### Description (what the task is asked to do)");
        sb.AppendLine(task.Description);
        sb.AppendLine();
        sb.AppendLine("### Directive (the task's own running notes)");
        sb.AppendLine(string.IsNullOrWhiteSpace(task.Directive) ? "(none)" : task.Directive);
        sb.AppendLine();

        sb.AppendLine("### Runs since the last change (oldest first)");
        sb.AppendLine("| started | cost | tier | outcome | tool calls | repeated fetches | report hash | skills |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in candidate.Runs)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.StartedAt:yyyy-MM-dd} | ${r.CostUsd:0.00} | {r.HighestTier?.ToString() ?? "?"} | " +
                $"{r.Outcome?.ToString() ?? "?"}{(r.Delegated ? " (delegated)" : "")}{(r.Gate is { } g ? $", gate {g}" : "")} | " +
                $"{r.ToolCalls?.ToString(CultureInfo.InvariantCulture) ?? "?"} | {r.DuplicateFetches?.ToString(CultureInfo.InvariantCulture) ?? "?"} | " +
                $"{r.ReplyHash ?? "-"} | {(r.SkillsUsed is { Count: > 0 } s ? string.Join(", ", s) : "-")} |");
        sb.AppendLine();

        // Skills: those the runs recorded, else those the patrol session's usage log shows.
        var skillNames = candidate.Runs
            .SelectMany(r => r.SkillsUsed ?? [])
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
        if (skillNames.Count == 0 && _skillUsageStore is not null)
        {
            var since = candidate.Runs[0].StartedAt;
            skillNames = (await _skillUsageStore.GetBySessionAsync(sessionId, ct))
                .Where(e => e.Timestamp >= since)
                .GroupBy(e => e.SkillName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();
        }

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_skillStore is not null)
        {
            var protectedNames = ProtectedSkillNames(await _skillStore.ListAsync());
            foreach (var name in skillNames.Take(EfficiencyMaxSkillsPerTask))
            {
                if (await _skillStore.GetAsync(name) is not { } skill)
                    continue;
                shown.Add(skill.Name);
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"### Skill: {skill.Name}{(protectedNames.Contains(skill.Name) ? " (code-owned: do not rewrite)" : "")}");
                sb.AppendLine(Truncate(skill.Content, EfficiencyMaxSkillChars));
                if (skill.Manifest is { Count: > 0 } manifest)
                {
                    sb.AppendLine("Resources:");
                    foreach (var res in manifest)
                        sb.AppendLine(CultureInfo.InvariantCulture,
                            $"- {res.Filename} ({res.Type}{(res.Provisional ? ", provisional" : "")}): {res.Description}");
                }
                sb.AppendLine();
            }
        }

        // What the latest run actually did, call by call.
        if (_toolCallLog is not null)
        {
            var last = candidate.Runs[^1];
            var calls = (await _toolCallLog.GetBySessionAsync(sessionId, ct))
                .Where(e => e.Timestamp >= last.StartedAt && e.Timestamp <= last.EndedAt)
                .OrderBy(e => e.Timestamp)
                .ToList();
            if (calls.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"### Tool calls of the latest run ({calls.Count})");
                foreach (var call in calls.Take(EfficiencyMaxToolCalls))
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"- {call.ToolName}({Truncate(call.ArgumentsSummary ?? "", 200)}){(call.Succeeded ? "" : " FAILED")}");
                if (calls.Count > EfficiencyMaxToolCalls)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- … {calls.Count - EfficiencyMaxToolCalls} more");
                sb.AppendLine();
            }
        }

        // What the runs reported.
        if (_savedResponseStore is not null)
        {
            var replies = (await _savedResponseStore.ListAsync(ct))
                .Where(r => r.Source == SavedResponseSources.Scheduled
                            && string.Equals(r.Label.Trim(), task.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.SavedAt)
                .Take(3)
                .ToList();
            foreach (var reply in replies)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"### Report of {reply.SavedAt:yyyy-MM-dd}");
                sb.AppendLine(Truncate(reply.Content, EfficiencyMaxReplyChars));
                sb.AppendLine();
            }
        }

        // Wisps the task already runs: the fixed fetch steps a saved wisp could take over.
        if (_wispExecutionLog is not null)
        {
            var wisps = (await _wispExecutionLog.QueryRecentAsync(
                    candidate.Runs[0].StartedAt, maxResults: int.MaxValue))
                .Where(r => r.Succeeded && string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(r.DefinitionBody))
                .GroupBy(r => r.ShapeHash ?? r.DefinitionHash)
                .Select(g => g.OrderByDescending(r => r.Timestamp).First())
                .Take(3)
                .ToList();
            foreach (var wisp in wisps)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"### Wisp the task ran: {wisp.Description}");
                sb.AppendLine("```json");
                sb.AppendLine(Truncate(wisp.DefinitionBody!, EfficiencyMaxWispBodyChars));
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return shown;
    }

    /// <summary>
    /// Applies the model's proposal for one task and records what changed with the task's median
    /// cost before it, so the Usage tab can show the effect. Returns the record, or null when
    /// nothing changed and nothing was suggested.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>A skill rewrite replaces only the body of a skill the prompt showed for this task, never
    ///   a code-owned one; its resources and manifest are kept.</item>
    ///   <item>A new wisp is attached provisional, so it is validated by its own runs like any other.</item>
    ///   <item>A gate is switched on only when its wisp is valid and only reads, and the task has no gate
    ///   a person set. Otherwise it is recorded as a suggestion.</item>
    /// </list>
    /// </remarks>
    internal async Task<ScheduledTaskOptimization?> ApplyEfficiencyProposalAsync(
        ScheduledTaskEfficiencyAdvisor.Candidate candidate,
        TaskEfficiencyProposalDto proposal,
        IReadOnlySet<string> rewritableSkills,
        CancellationToken ct)
    {
        var task = candidate.Task;
        var changes = new List<string>();
        string? suggestedGate = null;

        if (proposal.SkillRewrite is { } rewrite
            && !string.IsNullOrWhiteSpace(rewrite.Name)
            && !string.IsNullOrWhiteSpace(rewrite.Content)
            && _skillStore is not null)
        {
            var name = rewrite.Name.Trim();
            var protectedNames = ProtectedSkillNames(await _skillStore.ListAsync());
            if (protectedNames.Contains(name))
                LogCodeOwnedRefusal("scheduled-task efficiency", name);
            else if (!rewritableSkills.Contains(name))
                _logger.LogDebug("DreamService: scheduled-task efficiency — '{Skill}' was not shown for '{Task}'; not rewritten",
                    name, task.Name);
            else if (await _skillStore.GetAsync(name) is { } existing
                     && !string.Equals(existing.Content.Trim(), rewrite.Content.Trim(), StringComparison.Ordinal))
            {
                await _skillStore.SaveAsync(existing with
                {
                    Content = rewrite.Content.Trim(),
                    Summary = string.IsNullOrWhiteSpace(rewrite.Summary) ? existing.Summary : rewrite.Summary.Trim(),
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                changes.Add($"rewrote skill '{existing.Name}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(proposal.Directive)
            && !string.Equals(proposal.Directive.Trim(), task.Directive?.Trim(), StringComparison.Ordinal))
        {
            await _scheduledTaskStore!.UpdateDirectiveAsync(task.Name, proposal.Directive.Trim());
            changes.Add("updated the task directive");
        }

        if (proposal.NewWisp is { } wisp && await AttachEfficiencyWispAsync(task, wisp) is { } attached)
            changes.Add($"saved wisp {attached}");

        if (proposal.Gate is { } gate && !string.IsNullOrWhiteSpace(gate.Skill) && !string.IsNullOrWhiteSpace(gate.Resource))
        {
            var target = new TaskGate(gate.Skill.Trim(), gate.Resource.Trim()).Target;
            var (verdict, why) = await DecideEfficiencyGateAsync(task, gate);
            switch (verdict)
            {
                case GateVerdict.Enable:
                    var mode = ParseGateMode(gate.Mode);
                    var newGate = new TaskGate(gate.Skill.Trim(), gate.Resource.Trim(), mode,
                        EnabledBy: OptimizerGateOwner,
                        Reason: Truncate(proposal.Rationale ?? "set by the dream's efficiency pass", 300));
                    if (await _scheduledTaskStore!.UpdateGateAsync(task.Name, newGate))
                        changes.Add($"gated on wisp {target} ({(mode == TaskGateMode.Changed ? "changed" : "nonEmpty")})");
                    break;
                case GateVerdict.Suggest:
                    // A real wisp that may write: worth the user's look, and set_task_gate can switch it on.
                    suggestedGate = target;
                    _logger.LogInformation(
                        "DreamService: scheduled-task efficiency — gate {Target} for '{Task}' only suggested: {Why}",
                        target, task.Name, why);
                    break;
                default:
                    // Nothing the user could act on (no such wisp, an invalid one, their own gate in
                    // place): not recorded, so it neither misleads the Usage tab nor benches the task.
                    _logger.LogInformation(
                        "DreamService: scheduled-task efficiency — gate {Target} for '{Task}' refused: {Why}",
                        target, task.Name, why);
                    break;
            }
        }

        if (changes.Count == 0 && suggestedGate is null)
            return null;

        var record = new ScheduledTaskOptimization(
            _clock.Now, changes, candidate.MedianCostUsd,
            Rationale: string.IsNullOrWhiteSpace(proposal.Rationale) ? null : Truncate(proposal.Rationale.Trim(), 500),
            SuggestedGate: suggestedGate);
        await _scheduledTaskRunCosts!.RecordOptimizationAsync(task.Name, record, ct);
        return record;
    }

    /// <summary>Attaches a proposed wisp to its skill, provisional. Returns <c>skill/file</c>, or null when refused.</summary>
    private async Task<string?> AttachEfficiencyWispAsync(ScheduledTask task, NewWispDto wisp)
    {
        var body = wisp.BodyText();
        if (_skillStore is null || _savedWispRunner is null
            || string.IsNullOrWhiteSpace(wisp.Skill) || string.IsNullOrWhiteSpace(wisp.Filename) || body is null)
            return null;

        var skill = wisp.Skill.Trim();
        var filename = wisp.Filename.Trim();
        if (ProtectedSkillNames(await _skillStore.ListAsync()).Contains(skill))
        {
            LogCodeOwnedRefusal("scheduled-task efficiency", skill);
            return null;
        }

        var check = _savedWispRunner.Inspect(body);
        if (!check.Valid)
        {
            _logger.LogInformation("DreamService: scheduled-task efficiency — wisp {Skill}/{File} for '{Task}' is invalid: {Reason}",
                skill, filename, task.Name, check.Reason);
            return null;
        }

        var description = string.IsNullOrWhiteSpace(wisp.Description)
            ? $"Fetch step for scheduled task '{task.Name}'"
            : wisp.Description.Trim();
        var fingerprints = WispToolFingerprints.Capture(body, _mcpToolDirectory);
        var entry = new SkillResource(filename, SkillResourceType.Wisp, description,
            Provisional: true,
            CreatedAt: DateTimeOffset.UtcNow,
            VerifyHint: $"saved by the efficiency pass for scheduled task '{task.Name}'",
            DefinitionHash: check.ShapeHash,
            ToolFingerprints: fingerprints);
        var input = new SkillResourceInput(filename, SkillResourceType.Wisp, description, body,
            Provisional: true, VerifyHint: entry.VerifyHint, ToolFingerprints: fingerprints);
        return await _skillStore.AttachResourceAsync(skill, input, entry) ? $"{skill}/{filename}" : null;
    }

    private enum GateVerdict { Enable, Suggest, Refuse }

    /// <summary>
    /// Whether a proposed gate is switched on (a valid wisp that only reads), only suggested to the
    /// user (a valid wisp that may write), or refused (anything the user could not act on), and why.
    /// </summary>
    private async Task<(GateVerdict Verdict, string? Why)> DecideEfficiencyGateAsync(ScheduledTask task, GateProposalDto gate)
    {
        if (task.Gate is { } current && !string.Equals(current.EnabledBy, OptimizerGateOwner, StringComparison.OrdinalIgnoreCase))
            return (GateVerdict.Refuse, "the task has a gate a person set");
        if (_skillStore is null || _savedWispRunner is null)
            return (GateVerdict.Refuse, "wisps are not available to check it");

        var body = await _skillStore.GetResourceAsync(gate.Skill!.Trim(), gate.Resource!.Trim());
        if (body is null)
            return (GateVerdict.Refuse, "the wisp resource does not exist");

        var check = _savedWispRunner.Inspect(body);
        if (!check.Valid)
            return (GateVerdict.Refuse, $"the wisp is invalid: {check.Reason}");
        if (!check.ReadOnly)
            return (GateVerdict.Suggest, $"the wisp is not known to only read: {check.Reason}");
        return (GateVerdict.Enable, null);
    }

    private static TaskGateMode ParseGateMode(string? mode) =>
        string.Equals(mode?.Trim(), "changed", StringComparison.OrdinalIgnoreCase) ? TaskGateMode.Changed : TaskGateMode.NonEmpty;

    // ── DTOs ──────────────────────────────────────────────────────────────────

    internal sealed record ScheduledTaskEfficiencyResultDto
    {
        public List<TaskEfficiencyProposalDto>? Tasks { get; init; }
    }

    internal sealed record TaskEfficiencyProposalDto
    {
        public string? Task { get; init; }
        public SkillRewriteDto? SkillRewrite { get; init; }
        public string? Directive { get; init; }
        public NewWispDto? NewWisp { get; init; }
        public GateProposalDto? Gate { get; init; }
        public string? Rationale { get; init; }
    }

    internal sealed record SkillRewriteDto
    {
        public string? Name { get; init; }
        public string? Content { get; init; }
        public string? Summary { get; init; }
    }

    internal sealed record NewWispDto
    {
        public string? Skill { get; init; }
        public string? Filename { get; init; }
        public string? Description { get; init; }

        /// <summary>The wisp definition: a JSON object, or a string holding one.</summary>
        public JsonElement Body { get; init; }

        public string? BodyText() => Body.ValueKind switch
        {
            JsonValueKind.Object => Body.GetRawText(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(Body.GetString()) => Body.GetString(),
            _ => null,
        };
    }

    internal sealed record GateProposalDto
    {
        public string? Skill { get; init; }
        public string? Resource { get; init; }
        public string? Mode { get; init; }
    }

    internal const string BuiltInScheduledTaskEfficiencyDirective = """
        You are reviewing recurring scheduled tasks that cost real money on every run while doing
        little new: they report the same thing (or nothing) run after run, fetch the same data more
        than once, or make many tool calls. Your job is to make the next runs cheaper without making
        them miss anything the task exists to catch.

        For each task you get its description, its directive (notes it keeps for itself across runs),
        its recent runs (cost, tier, outcome, tool calls, repeated fetches, a hash of each report, the
        skills it used), the skills' text and resources, the latest run's tool calls, its last reports,
        and wisps it already runs.

        Look for, in order of payoff:
        1. Work redone every run: re-reading the whole mailbox or calendar instead of what arrived
           since the last run, re-judging items already judged. Bound searches by date. A wisp can use
           {{last_run_at}} (ISO 8601) and {{last_run_date}} (yyyy/MM/dd), filled from the task's last fire.
        2. Items already decided: keep a short skip-list of their ids in the directive so the next run
           ignores them.
        3. Fixed fetch steps the LLM performs the same way every run: move them into a saved wisp
           (direct steps only) and gate the task on it, so a run that finds nothing skips the LLM.
        4. Skill text that sends the run on detours: tighten it. Keep every rule that protects the user.

        Respond with JSON only:
        {
          "tasks": [
            {
              "task": "<task name>",
              "skillRewrite": { "name": "<a skill shown for this task>", "content": "<full new markdown>", "summary": "<optional>" },
              "directive": "<full new directive text>",
              "newWisp": { "skill": "<skill to attach it to>", "filename": "<name>.json", "description": "<what it fetches>",
                           "body": { "description": "...", "steps": [ ... ] } },
              "gate": { "skill": "<skill>", "resource": "<wisp file>", "mode": "nonEmpty" },
              "rationale": "<one or two sentences: what was wasted and what this changes>"
            }
          ]
        }

        Every field except "task" is optional; omit what you would not change. A gate may name the new
        wisp or one the skill already has. Use "changed" mode only when the wisp's output repeats the
        same items until something new arrives. Never rewrite a skill marked code-owned. A gate is
        switched on only when every step of its wisp is a direct MCP call whose tool name starts with a
        read verb (search_, list_, get_, ...) and has no write verb, or a web_search / web_browse step;
        any other gate is only recorded as a suggestion. The gate passes when any step finds results.
        When nothing would clearly help, return {"tasks": []}.
        """;
}
