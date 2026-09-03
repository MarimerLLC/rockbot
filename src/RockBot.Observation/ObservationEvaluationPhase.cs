using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Observation;

/// <summary>
/// Default <see cref="IObservationEvaluationPhase"/>. Aging is deterministic
/// in-memory work; evaluation is one Higher-tier LLM call only when there
/// are candidates eligible for promotion. Markdown is regenerated from the
/// resulting state and a snapshot is appended. Promoted theories are also
/// published to <see cref="ILongTermMemory"/> so they participate in the
/// host's hybrid-search index and surface via <c>SearchMemory</c>; aged-out
/// theories have their memory entries deleted.
/// </summary>
internal sealed class ObservationEvaluationPhase(
    IObservationEvaluator evaluator,
    IObservationStateStore stateStore,
    ILongTermMemory longTermMemory,
    ILogger<ObservationEvaluationPhase> logger) : IObservationEvaluationPhase
{
    /// <summary>
    /// Importance score assigned to memory entries published for promoted
    /// theories. Above the default (0.5) — promoted theories represent
    /// reinforced observations — but not maximum, so they do not crowd out
    /// hand-saved high-importance memories.
    /// </summary>
    private const float TheoryMemoryImportance = 0.7f;
    public async Task<EvaluationPhaseResult> ExecuteAsync(
        ObservationTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var state = await stateStore.LoadAsync(target, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        // Aging — deterministic, in-memory. Capture memory entry IDs of aged
        // theories so we can delete them from the long-term store after the
        // JSON save succeeds.
        var candidatesAged = AgeCandidates(state, target, now);
        var (theoriesAged, agedMemoryEntryIds) = AgeTheories(state, target, now);

        cancellationToken.ThrowIfCancellationRequested();

        // Evaluation — only run if there are candidates above threshold.
        var eligible = state.Candidates
            .Where(c => c.Count >= target.PromotionThreshold)
            .ToList();

        var promoted = 0;
        var refined = 0;
        var rejected = 0;
        var promotedTheories = new List<Theory>();

        if (eligible.Count > 0)
        {
            var verdicts = await evaluator.EvaluateAsync(
                target, eligible, state.Theories, cancellationToken).ConfigureAwait(false);

            (promoted, refined, rejected, promotedTheories) = ApplyVerdicts(state, verdicts, now);
        }

        // Regenerate markdown
        cancellationToken.ThrowIfCancellationRequested();
        var markdown = MarkdownRenderer.Render(target, state, now);

        // Snapshot — append + evict oldest beyond cap
        AppendSnapshot(state, target, markdown, now);

        // Atomic writes: JSON state first (single source of truth), then
        // markdown. Memory side-effects come AFTER the JSON commit so a
        // failure during JSON save leaves the long-term store unchanged.
        // Memory failures after this point leave orphan/missing entries
        // recoverable on a future dream's reconciliation pass.
        await stateStore.SaveAsync(target, state, cancellationToken).ConfigureAwait(false);
        await WriteMarkdownAtomicAsync(target.OutputMarkdownPath, markdown, cancellationToken)
            .ConfigureAwait(false);

        // Long-term memory side-effects: best-effort. Per-entry exceptions
        // are logged and swallowed so one broken memory write does not
        // prevent the rest from completing. Cancellation still propagates.
        foreach (var memoryId in agedMemoryEntryIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryDeleteMemoryAsync(memoryId, target, cancellationToken).ConfigureAwait(false);
        }
        foreach (var theory in promotedTheories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TrySaveTheoryMemoryAsync(target, theory, now, cancellationToken).ConfigureAwait(false);
        }

        var result = new EvaluationPhaseResult(
            CandidatesAged: candidatesAged,
            TheoriesAged: theoriesAged,
            CandidatesEvaluated: eligible.Count,
            CandidatesPromoted: promoted,
            CandidatesRefined: refined,
            CandidatesRejected: rejected,
            MarkdownRegenerated: true,
            StateWritten: true);

        logger.LogInformation(
            "Observation: target {Target} phase 2 complete — " +
            "aged {CandidatesAged}/{TheoriesAged} candidates/theories, " +
            "evaluated {Eligible}, promoted {Promoted}, refined {Refined}, rejected {Rejected}",
            target.Name,
            candidatesAged, theoriesAged,
            eligible.Count, promoted, refined, rejected);

        return result;
    }

    private async Task TryDeleteMemoryAsync(string memoryEntryId, ObservationTarget target, CancellationToken ct)
    {
        try
        {
            // Archive rather than delete. A theory ages out because it stopped being re-observed
            // in a window, which is weak evidence that it was wrong — the behaviour it described
            // may simply not have come up.
            await longTermMemory.ArchiveAsync(memoryEntryId, "observation theory aged out", ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Observation: failed to archive memory entry {MemoryEntryId} for aged theory in target {Target}",
                memoryEntryId, target.Name);
        }
    }

    private async Task TrySaveTheoryMemoryAsync(
        ObservationTarget target, Theory theory, DateTimeOffset now, CancellationToken ct)
    {
        if (theory.MemoryEntryId is null) return;

        var entry = new MemoryEntry(
            Id: theory.MemoryEntryId,
            Content: theory.Text,
            Category: $"observation/theory/{target.Name}",
            Tags: new[] { "observation", target.Name },
            CreatedAt: now,
            UpdatedAt: now,
            Metadata: null,
            ImportanceScore: TheoryMemoryImportance);

        try
        {
            await longTermMemory.SaveAsync(entry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Observation: failed to publish memory entry {MemoryEntryId} for promoted theory {TheoryId} in target {Target}; theory remains in JSON state but is not searchable until a future reconciliation",
                theory.MemoryEntryId, theory.Id, target.Name);
        }
    }

    private static int AgeCandidates(ObservationState state, ObservationTarget target, DateTimeOffset now)
    {
        var threshold = now - TimeSpan.FromDays(target.CandidateAgingWindowDays);
        var before = state.Candidates.Count;
        state.Candidates.RemoveAll(c => c.LastSeen < threshold);
        return before - state.Candidates.Count;
    }

    private static (int Aged, IReadOnlyList<string> AgedMemoryEntryIds) AgeTheories(
        ObservationState state, ObservationTarget target, DateTimeOffset now)
    {
        var threshold = now - TimeSpan.FromDays(target.TheoryAgingWindowDays);
        var aged = state.Theories.Where(t => t.LastReinforced < threshold).ToList();
        var memoryEntryIds = aged
            .Where(t => !string.IsNullOrEmpty(t.MemoryEntryId))
            .Select(t => t.MemoryEntryId!)
            .ToList();
        foreach (var t in aged)
            state.Theories.Remove(t);
        return (aged.Count, memoryEntryIds);
    }

    private static (int Promoted, int Refined, int Rejected, List<Theory> PromotedTheories) ApplyVerdicts(
        ObservationState state,
        IReadOnlyList<EvaluationVerdict> verdicts,
        DateTimeOffset now)
    {
        var promoted = 0;
        var refined = 0;
        var rejected = 0;
        var promotedTheories = new List<Theory>();

        foreach (var v in verdicts)
        {
            var candidate = state.Candidates.FirstOrDefault(c => c.Id == v.CandidateId);
            if (candidate is null) continue;

            switch (v.Action)
            {
                case EvaluationAction.Promote:
                    var theory = new Theory
                    {
                        Id = "thry_" + Guid.NewGuid().ToString("N")[..12],
                        Text = string.IsNullOrWhiteSpace(v.RefinedText) ? candidate.Text : v.RefinedText,
                        PromotedAt = now,
                        LastReinforced = candidate.LastSeen,
                        MemoryEntryId = "obs_" + Guid.NewGuid().ToString("N")[..12],
                    };
                    theory.SourceCandidateIds.Add(candidate.Id);
                    foreach (var r in candidate.References)
                        theory.References.Add(r);
                    state.Theories.Add(theory);
                    state.Candidates.Remove(candidate);
                    promotedTheories.Add(theory);
                    promoted++;
                    break;

                case EvaluationAction.Refine:
                    if (!string.IsNullOrWhiteSpace(v.RefinedText))
                    {
                        candidate.Text = v.RefinedText;
                        refined++;
                    }
                    break;

                case EvaluationAction.Reject:
                    state.Candidates.Remove(candidate);
                    rejected++;
                    break;

                case EvaluationAction.Unspecified:
                default:
                    // Leave the candidate alone; it remains eligible next time.
                    break;
            }
        }

        return (promoted, refined, rejected, promotedTheories);
    }

    private static void AppendSnapshot(
        ObservationState state,
        ObservationTarget target,
        string markdown,
        DateTimeOffset takenAt)
    {
        state.Snapshots.Add(new Snapshot(takenAt, markdown));
        while (state.Snapshots.Count > target.SnapshotRetentionCount)
            state.Snapshots.RemoveAt(0);
    }

    private static async Task WriteMarkdownAtomicAsync(
        string path,
        string markdown,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, markdown, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }
}
