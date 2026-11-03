using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.AdvisorCouncil.Schema;
using RockBot.Host;
using RockBot.Llm;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Decides, once the persona views are in, whether cross-critique is worth its cost
/// (<see cref="DecisionPurposes.CouncilCritique"/>).
/// </summary>
/// <remarks>
/// <para>
/// Select decides <c>critique</c> before anyone has spoken, from the question alone. Critique costs
/// one High-tier call per persona, and when the views already converge it mostly restates them.
/// One decision-model call over the actual views can tell: critique runs unless the model is
/// fairly sure they agree. The bar is set low on purpose (run at p ≥ 0.35): a missed disagreement
/// costs the council its tensions, a needless critique only costs money.
/// </para>
/// <para>
/// Not consulted when the caller forced <c>critique: on</c>. When the model is unavailable,
/// critique runs, as it did before.
/// </para>
/// </remarks>
internal sealed class CritiqueGate(
    IDecisionService? decisions = null,
    ILogger<CritiqueGate>? logger = null)
{
    private const double DefaultThreshold = 0.35;
    private const int MaxViewChars = 1500;
    private const int MaxQuestionChars = 2000;

    internal const string QuestionName = "views_disagree";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>True when the decision model is configured and this use is on.</summary>
    public bool IsEnabled => _decisions.IsEnabled(DecisionPurposes.CouncilCritique);

    /// <summary>False only when the model is confident the views already agree.</summary>
    public async Task<bool> ShouldCritiqueAsync(CouncilRequest request, IReadOnlyList<PersonaView> views, CancellationToken ct)
    {
        if (!IsEnabled)
            return true;

        var contributing = views.Where(v => v.Status == PersonaStatus.Ok).ToList();
        if (contributing.Count < 2)
            return true; // nothing to compare; the orchestrator's own rules decide

        var result = await _decisions.DecideAsync(BuildRequest(request, contributing), ct).ConfigureAwait(false);
        if (result?.Probability(QuestionName) is not { } p)
            return true;

        var threshold = _decisions.ThresholdFor(DecisionPurposes.CouncilCritique, DefaultThreshold);
        var run = p >= threshold;
        _logger.LogInformation(
            "Decision model: council views disagree? p={Probability:0.000} threshold={Threshold:0.00} ({Count} views) → {Outcome}",
            p, threshold, contributing.Count, run ? "critique" : "skip critique");
        return run;
    }

    internal static DecisionRequest BuildRequest(CouncilRequest request, IReadOnlyList<PersonaView> views) =>
        DecisionRequest.Single(DecisionPurposes.CouncilCritique,
            new Dictionary<string, object?>
            {
                ["question"] = Truncate(request.RenderBrief(), MaxQuestionChars),
                ["advisor_views"] = views.Select(v => new Dictionary<string, object?>
                {
                    ["advisor"] = v.Id,
                    ["view"] = Truncate(v.View, MaxViewChars),
                }).ToList(),
            },
            QuestionName,
            new NoulQuestion(
                "Do these advisor views substantively disagree, so that asking each advisor to respond to the " +
                "others would change the advice?",
                WhenTrue: "They recommend different courses of action, weigh the main risk or benefit in opposite " +
                          "directions, or make conflicting claims about the facts",
                WhenFalse: "They reach broadly the same recommendation from different angles; the differences are " +
                           "of emphasis, not substance"));

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// Before a persona's research call, checks whether research the council already holds answers it
/// (<see cref="DecisionPurposes.CouncilResearchReuse"/>), and if so hands that back instead.
/// </summary>
/// <remarks>
/// <para>
/// Each research call is a whole research run on the research server (High tier, a minute or
/// more), and personas often ask for what the shared pre-research already covers. The candidates
/// are the run's research entries in working memory: <c>council/{runId}/shared</c> and every
/// <c>council/{runId}/{persona}/research/{n}</c>. One decision call asks, per candidate, whether it
/// already answers the new question; the best one at p ≥ 0.7 (a high bar: a wrong reuse leaves the
/// persona arguing from the wrong facts) is returned in place of a new call.
/// </para>
/// <para>
/// When the model is unavailable, or no candidate clears the bar, the call goes ahead as before.
/// </para>
/// </remarks>
internal sealed class ResearchReuse(
    IWorkingMemory workingMemory,
    IDecisionService? decisions = null,
    ILogger<ResearchReuse>? logger = null)
{
    private const double DefaultThreshold = 0.7;
    private const int MaxCandidates = 8;
    private const int MaxCandidateChars = 1500;

    internal const string ReusedPrefix = "(Reused: the council already researched this; no new research call was made.)";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>True when the decision model is configured and this use is on.</summary>
    public bool IsEnabled => _decisions.IsEnabled(DecisionPurposes.CouncilResearchReuse);

    /// <summary>Existing findings that answer <paramref name="question"/>, or null to research it.</summary>
    public async Task<string?> FindAsync(string question, string runId, CancellationToken ct)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(question))
            return null;

        IReadOnlyList<WorkingMemoryEntry> candidates;
        try
        {
            candidates = [.. (await workingMemory.ListAsync($"council/{runId}/"))
                .Where(e => IsResearchKey(e.Key) && !string.IsNullOrWhiteSpace(e.Value))
                .OrderByDescending(e => e.StoredAt)
                .Take(MaxCandidates)];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Council {RunId}: could not list research for reuse", runId);
            return null;
        }
        if (candidates.Count == 0)
            return null;

        var result = await _decisions.DecideAsync(BuildRequest(question, candidates), ct).ConfigureAwait(false);
        if (result is null)
            return null;

        var threshold = _decisions.ThresholdFor(DecisionPurposes.CouncilResearchReuse, DefaultThreshold);
        var (best, p) = candidates
            .Select((c, i) => (Entry: c, P: result.Probability(CandidateName(i)) ?? 0))
            .MaxBy(x => x.P);

        _logger.LogInformation(
            "Decision model: council research already answers \"{Question}\"? best={Key} p={Probability:0.000} " +
            "threshold={Threshold:0.00} ({Count} candidate(s)) → {Outcome}",
            Truncate(question, 120), best.Key, p, threshold, candidates.Count, p >= threshold ? "reuse" : "research");

        return p >= threshold ? $"{ReusedPrefix}\n\n{best.Value}" : null;
    }

    internal static string CandidateName(int index) => $"finding_{index + 1}_answers";

    internal static bool IsResearchKey(string key) =>
        key.EndsWith("/shared", StringComparison.Ordinal) || key.Contains("/research/", StringComparison.Ordinal);

    internal static DecisionRequest BuildRequest(string question, IReadOnlyList<WorkingMemoryEntry> candidates)
    {
        var questions = new Dictionary<string, DecisionQuestion>();
        for (var i = 0; i < candidates.Count; i++)
        {
            questions[CandidateName(i)] = new NoulQuestion(
                $"Does finding_{i + 1} already answer new_question, so that researching it again would add little?",
                WhenTrue: "The finding covers what new_question asks, with the facts needed to answer it",
                WhenFalse: "The finding is about something else, or touches the topic without the specific facts " +
                           "new_question asks for");
        }

        return new DecisionRequest(DecisionPurposes.CouncilResearchReuse,
            new Dictionary<string, object?>
            {
                ["new_question"] = question,
                ["findings"] = candidates.Select((c, i) => new Dictionary<string, object?>
                {
                    ["id"] = $"finding_{i + 1}",
                    ["text"] = Truncate(c.Value, MaxCandidateChars),
                }).ToList(),
            },
            questions);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
