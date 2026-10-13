using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Decides whether a user message corrects the agent's previous reply. A correction is written as
/// a <see cref="FeedbackSignalType.Correction"/> entry, which marks the session at risk and feeds
/// the dream's skill rewrites.
/// </summary>
/// <remarks>
/// The old check was one regex, <c>\b(no[,\s]|…|actually[,\s]|…|try again)\b</c>, which counted
/// "no problem", "I have no idea" and any "actually, …" as corrections, and missed "that's not
/// what I asked". Now:
/// <list type="number">
///   <item>No previous agent reply, or a bare acknowledgement → not a correction.</item>
///   <item>An unambiguous phrase ("that's wrong", "you were wrong", "that didn't work") → correction.</item>
///   <item>A weaker cue (no, not, actually, instead, meant, try again, …) → ask the decision model
///   (<see cref="DecisionPurposes.Correction"/>) with the agent's reply and the user's message.
///   If it can't answer, the old regex decides, so behaviour never gets worse than before.</item>
///   <item>No cue at all → not a correction, and no call.</item>
/// </list>
/// </remarks>
public sealed partial class CorrectionDetector(
    IDecisionService? decisions = null,
    ILogger<CorrectionDetector>? logger = null)
{
    // Probed 2026-10-13: real corrections scored >= 0.999; the highest non-correction ("I have no
    // idea what time it starts" after a scheduling reply) 0.651.
    private const double DefaultThreshold = 0.7;
    private const int MaxFieldChars = 3000;
    internal const string QuestionName = "corrects_agent";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>The pre-decision-model regex, kept as the fallback for weak cues.</summary>
    [GeneratedRegex(@"\b(no[,\s]|that'?s?\s+(wrong|incorrect|not right)|you were wrong|actually[,\s]|that didn'?t work|try again)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex LegacyCorrectionRegex();

    /// <summary>Phrases that are a correction on their own.</summary>
    [GeneratedRegex(@"\b(?:that['’]?s|that\s+is|this\s+is|it['’]?s|you['’]?re|you\s+are)\s+(?:wrong|incorrect|not\s+(?:right|correct|true|what\s+I\s+(?:asked|said|meant|wanted)))\b" +
                    @"|\byou\s+(?:were|got\s+(?:it|that))\s+wrong\b|\bthat\s+(?:didn['’]?t|did\s+not)\s+work\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex DefiniteCorrectionRegex();

    /// <summary>Cues worth asking about: pushback words that also appear in ordinary messages.</summary>
    [GeneratedRegex(@"\b(?:no|nope|not|wrong|incorrect|actually|instead|meant|mistake|try\s+again|redo|again|didn['’]?t|isn['’]?t|wasn['’]?t|aren['’]?t|doesn['’]?t|don['’]?t|shouldn['’]?t|should\s+(?:have|be)|that['’]?s\s+not)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex CueRegex();

    /// <summary>The deterministic verdict, before any model call.</summary>
    public enum Verdict
    {
        /// <summary>Not a correction.</summary>
        No,
        /// <summary>A correction.</summary>
        Yes,
        /// <summary>Unsure: ask the model, falling back to the legacy regex.</summary>
        Ask,
    }

    /// <summary>The deterministic first pass.</summary>
    public static Verdict Classify(string userMessage, string? previousAgentMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(previousAgentMessage))
            return Verdict.No;
        if (CompletionEvalTriggers.IsTrivialChat(userMessage))
            return Verdict.No;
        if (DefiniteCorrectionRegex().IsMatch(userMessage))
            return Verdict.Yes;
        return CueRegex().IsMatch(userMessage) ? Verdict.Ask : Verdict.No;
    }

    /// <summary>True when <paramref name="userMessage"/> corrects <paramref name="previousAgentMessage"/>.</summary>
    public async Task<bool> IsCorrectionAsync(string userMessage, string? previousAgentMessage,
        CancellationToken cancellationToken = default)
    {
        var verdict = Classify(userMessage, previousAgentMessage);
        if (verdict != Verdict.Ask) return verdict == Verdict.Yes;

        var fallback = LegacyCorrectionRegex().IsMatch(userMessage);
        if (!_decisions.IsEnabled(DecisionPurposes.Correction)) return fallback;

        var result = await _decisions.DecideAsync(BuildRequest(userMessage, previousAgentMessage!), cancellationToken)
            .ConfigureAwait(false);
        if (result?.Probability(QuestionName) is not { } p)
            return fallback;

        var threshold = _decisions.ThresholdFor(DecisionPurposes.Correction, DefaultThreshold);
        var isCorrection = p >= threshold;
        _logger.LogInformation(
            "Decision model: correction? p={Probability:0.000} threshold={Threshold:0.00} → {Outcome} (legacy regex said {Legacy})",
            p, threshold, isCorrection ? "correction" : "not a correction", fallback ? "correction" : "not a correction");
        return isCorrection;
    }

    internal static DecisionRequest BuildRequest(string userMessage, string previousAgentMessage) =>
        DecisionRequest.Single(DecisionPurposes.Correction,
            new Dictionary<string, object?>
            {
                ["agent_reply"] = Truncate(previousAgentMessage),
                ["user_message"] = Truncate(userMessage),
            },
            QuestionName,
            new NoulQuestion(
                "Is user_message telling the assistant that agent_reply was wrong, incomplete, misunderstood the " +
                "request, or did not work?",
                WhenTrue: "The user points out a mistake, says the result is wrong or not what they asked for, " +
                          "or asks the assistant to redo it differently",
                WhenFalse: "The user asks something new, adds information, agrees, declines an offer, or says " +
                           "\"no\" to a question without saying the assistant got anything wrong"));

    private static string Truncate(string text) => text.Length <= MaxFieldChars ? text : text[..MaxFieldChars] + "…";
}
