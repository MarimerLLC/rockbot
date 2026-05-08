using Microsoft.Extensions.Logging;

namespace RockBot.Observation;

/// <summary>
/// Default <see cref="IObservationEvaluationPhase"/>. Aging is deterministic
/// in-memory work; evaluation is one Higher-tier LLM call only when there
/// are candidates eligible for promotion. Markdown is regenerated from the
/// resulting state and a snapshot is appended.
/// </summary>
internal sealed class ObservationEvaluationPhase(
    IObservationEvaluator evaluator,
    IObservationStateStore stateStore,
    ILogger<ObservationEvaluationPhase> logger) : IObservationEvaluationPhase
{
    public async Task<EvaluationPhaseResult> ExecuteAsync(
        ObservationTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var state = await stateStore.LoadAsync(target, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        // Aging — deterministic, in-memory
        var candidatesAged = AgeCandidates(state, target, now);
        var theoriesAged = AgeTheories(state, target, now);

        cancellationToken.ThrowIfCancellationRequested();

        // Evaluation — only run if there are candidates above threshold.
        var eligible = state.Candidates
            .Where(c => c.Count >= target.PromotionThreshold)
            .ToList();

        var promoted = 0;
        var refined = 0;
        var rejected = 0;

        if (eligible.Count > 0)
        {
            var verdicts = await evaluator.EvaluateAsync(
                target, eligible, state.Theories, cancellationToken).ConfigureAwait(false);

            (promoted, refined, rejected) = ApplyVerdicts(state, verdicts, now);
        }

        // Regenerate markdown
        cancellationToken.ThrowIfCancellationRequested();
        var markdown = MarkdownRenderer.Render(target, state, now);

        // Snapshot — append + evict oldest beyond cap
        AppendSnapshot(state, target, markdown, now);

        // Atomic writes: state first, then markdown. Both files end up
        // mutually consistent because we hold the rendered markdown in
        // memory before either write.
        await stateStore.SaveAsync(target, state, cancellationToken).ConfigureAwait(false);
        await WriteMarkdownAtomicAsync(target.OutputMarkdownPath, markdown, cancellationToken)
            .ConfigureAwait(false);

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

    private static int AgeCandidates(ObservationState state, ObservationTarget target, DateTimeOffset now)
    {
        var threshold = now - TimeSpan.FromDays(target.CandidateAgingWindowDays);
        var before = state.Candidates.Count;
        state.Candidates.RemoveAll(c => c.LastSeen < threshold);
        return before - state.Candidates.Count;
    }

    private static int AgeTheories(ObservationState state, ObservationTarget target, DateTimeOffset now)
    {
        var threshold = now - TimeSpan.FromDays(target.TheoryAgingWindowDays);
        var before = state.Theories.Count;
        state.Theories.RemoveAll(t => t.LastReinforced < threshold);
        return before - state.Theories.Count;
    }

    private static (int Promoted, int Refined, int Rejected) ApplyVerdicts(
        ObservationState state,
        IReadOnlyList<EvaluationVerdict> verdicts,
        DateTimeOffset now)
    {
        var promoted = 0;
        var refined = 0;
        var rejected = 0;

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
                    };
                    theory.SourceCandidateIds.Add(candidate.Id);
                    foreach (var r in candidate.References)
                        theory.References.Add(r);
                    state.Theories.Add(theory);
                    state.Candidates.Remove(candidate);
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

        return (promoted, refined, rejected);
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
