using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Llm;

namespace RockBot.ResearchAgent;

/// <summary>
/// The completion check for a quick research run, asked of the decision model
/// (<see cref="DecisionPurposes.ResearchAnswer"/>) instead of the loop's LLM completion evaluator.
/// </summary>
/// <remarks>
/// <para>
/// The loop's evaluator is built for the primary agent's turns: it fires on keyword triggers (a
/// research answer tripped <c>claimed-change</c>, though research changes nothing) and costs a
/// Low-tier call before any re-prompt. What a quick answer needs checking for is narrower: does it
/// answer the question, and did it keep to "a few short paragraphs"? The one time the evaluator
/// caught something on a quick run, it was length (4,595 chars, cut to 1,984). One decision call
/// answers both, in a second or so.
/// </para>
/// <para>
/// Fails open: when the model is unavailable or its answer can't be read, the answer stands.
/// </para>
/// </remarks>
internal sealed class ResearchAnswerCheck(
    IDecisionService? decisions = null,
    ILogger<ResearchAnswerCheck>? logger = null)
{
    private const double DefaultThreshold = 0.5;
    private const int MaxAnswerChars = 8000;

    internal const string MissesQuestion = "misses_question";
    internal const string TooLong = "too_long";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>True when the decision model is configured and this use is on.</summary>
    public bool IsEnabled => _decisions.IsEnabled(DecisionPurposes.ResearchAnswer);

    /// <summary>What the check found: nothing to fix, or the guidance for one rewrite.</summary>
    internal sealed record Verdict(bool Rewrite, string? Guidance)
    {
        public static Verdict Fine { get; } = new(false, null);
    }

    /// <summary>Judges <paramref name="answer"/> to a quick lookup of <paramref name="request"/>.</summary>
    public async Task<Verdict> CheckAsync(ResearchRequest request, string answer, CancellationToken cancellationToken)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(answer))
            return Verdict.Fine;

        var result = await _decisions.DecideAsync(BuildRequest(request, answer), cancellationToken).ConfigureAwait(false);
        if (result is null)
            return Verdict.Fine;

        var threshold = _decisions.ThresholdFor(DecisionPurposes.ResearchAnswer, DefaultThreshold);
        var misses = result.Probability(MissesQuestion) ?? 0;
        var tooLong = result.Probability(TooLong) ?? 0;

        var guidance = new List<string>();
        if (misses >= threshold)
            guidance.Add("Answer the question directly from what you found. If your sources did not settle it, " +
                         "say so plainly and say what is still unknown.");
        if (tooLong >= threshold)
            guidance.Add("This was a QUICK lookup: rewrite the answer as a few short paragraphs. Keep the " +
                         "conclusion, the facts that support it and what you could not check; drop the rest.");

        _logger.LogInformation(
            "Decision model: quick research answer ({Chars} chars) misses-question={Misses:0.000} too-long={TooLong:0.000} " +
            "threshold={Threshold:0.00} → {Outcome}",
            answer.Length, misses, tooLong, threshold, guidance.Count == 0 ? "fine" : "rewrite");

        return guidance.Count == 0
            ? Verdict.Fine
            : new Verdict(true, string.Join(" ", guidance) + " Do not call any tools.");
    }

    internal static DecisionRequest BuildRequest(ResearchRequest request, string answer) =>
        new(DecisionPurposes.ResearchAnswer,
            new Dictionary<string, object?>
            {
                ["research_question"] = request.Question,
                ["interpretation_to_research"] = request.Interpretation,
                ["requested_format"] = "a quick lookup: a few short paragraphs",
                ["answer_length_chars"] = answer.Length,
                ["answer"] = answer.Length <= MaxAnswerChars ? answer : answer[..MaxAnswerChars] + "…",
            },
            new Dictionary<string, DecisionQuestion>
            {
                [MissesQuestion] = new NoulQuestion(
                    "Does answer fail to answer research_question (or the interpretation_to_research, when " +
                    "one is given)?",
                    WhenTrue: "It is off topic, answers a different question, only describes what it would " +
                              "research, or never reaches an answer",
                    WhenFalse: "It answers the question, even if it says the evidence is mixed or that some " +
                               "points could not be checked"),
                [TooLong] = new NoulQuestion(
                    "The requested_format is a few short paragraphs. Is answer much longer than that?",
                    WhenTrue: "It is a long report: many sections or headings, long bullet lists, or several " +
                              "times the length of a few short paragraphs",
                    WhenFalse: "It is a few short paragraphs, perhaps with a short list of points or sources"),
            });
}
