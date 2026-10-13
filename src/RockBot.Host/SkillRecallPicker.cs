using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Picks which recalled skill, if any, gets its full body injected on a turn. Skill search
/// (BM25 + embeddings) ranks the candidates; the rank-1 skill's full instructions were injected with
/// no relevance check, and it is then marked recalled for the session, so a wrong pick is never
/// corrected. The decision model (<see cref="DecisionPurposes.SkillRecall"/>) is asked to choose among
/// the newly recalled skills or "none". A confident pick wins; otherwise rank-1, as before.
/// </summary>
public sealed class SkillRecallPicker(
    IDecisionService? decisions = null,
    ILogger<SkillRecallPicker>? logger = null)
{
    private const double DefaultConfidence = 0.6;
    private const int MaxMessageChars = 2000;
    private const int MaxCandidates = 5;
    internal const string QuestionName = "skill";
    internal const string None = "none";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>
    /// The index in <paramref name="candidates"/> of the skill to inject in full, -1 for none, or
    /// 0 (rank-1, the previous behaviour) when the model is unavailable or unsure.
    /// </summary>
    public async Task<int> PickAsync(string userMessage, IReadOnlyList<Skill> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0) return -1;
        if (!_decisions.IsEnabled(DecisionPurposes.SkillRecall)) return 0;

        var asked = candidates.Take(MaxCandidates).ToList();
        var result = await _decisions.DecideAsync(BuildRequest(userMessage, asked), cancellationToken).ConfigureAwait(false);
        if (result?.Answers.GetValueOrDefault(QuestionName) is not { Choice: { } choice } answer)
            return 0;

        var confidence = answer.Probabilities?.GetValueOrDefault(choice) ?? answer.Confidence ?? 0;
        var threshold = _decisions.ThresholdFor(DecisionPurposes.SkillRecall, DefaultConfidence);
        var index = choice == None ? -1 : asked.FindIndex(s => s.Name.Equals(choice, StringComparison.OrdinalIgnoreCase));
        var pick = confidence < threshold || (index < 0 && choice != None) ? 0 : index;

        _logger.LogInformation(
            "Decision model: skill to inject for this turn — rank-1 {Rank1}, model {Choice} p={Confidence:0.000} " +
            "threshold={Threshold:0.00} → {Outcome}",
            asked[0].Name, choice, confidence, threshold, pick < 0 ? "none" : asked[pick].Name);
        return pick;
    }

    internal static DecisionRequest BuildRequest(string userMessage, IReadOnlyList<Skill> candidates)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var skill in candidates)
            options[skill.Name] = string.IsNullOrWhiteSpace(skill.Summary) ? skill.Name : skill.Summary;
        options[None] = "None of these skills is about what the user is asking for";

        return DecisionRequest.Single(DecisionPurposes.SkillRecall,
            userMessage.Length <= MaxMessageChars ? userMessage : userMessage[..MaxMessageChars] + "…",
            QuestionName,
            new ChoiceQuestion(
                "An AI assistant has stored skills (step-by-step instructions for recurring tasks). Which skill's " +
                "instructions would the assistant need to handle this user message?",
                options));
    }
}
