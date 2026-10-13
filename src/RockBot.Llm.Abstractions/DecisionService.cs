namespace RockBot.Llm;

/// <summary>
/// A fast, cheap classifier that answers bounded questions about text or JSON with probabilities
/// instead of generated prose — a "systemone" decision model such as Microsoft-Decision-1. Callers
/// keep their deterministic check (regex, keyword list, threshold) as the first pass and ask this
/// service only about what that check cannot settle; when the service is disabled or fails, they
/// keep the deterministic answer.
/// </summary>
/// <remarks>
/// Implementations: <c>FoundryDecisionService</c> (RockBot.Host) and <see cref="NullDecisionService"/>.
/// Swap by registering another <see cref="IDecisionService"/> before the host's defaults.
/// </remarks>
public interface IDecisionService
{
    /// <summary>
    /// True when a model is configured and <paramref name="purpose"/> (a <see cref="DecisionPurposes"/>
    /// value) is not switched off. Check it before building a request.
    /// </summary>
    bool IsEnabled(string purpose);

    /// <summary>
    /// The decision threshold configured for <paramref name="purpose"/>, or <paramref name="fallback"/>.
    /// </summary>
    double ThresholdFor(string purpose, double fallback);

    /// <summary>
    /// Asks the model. Returns null when it is disabled, times out, is rate limited, or returns
    /// something unreadable — the caller then keeps its deterministic answer.
    /// </summary>
    Task<DecisionResult?> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The uses of <see cref="IDecisionService"/>, for configuration, logs and metrics.</summary>
public static class DecisionPurposes
{
    /// <summary>Consequential-action gate: did the user ask for this external change?</summary>
    public const string ActionGate = "ActionGate";

    /// <summary>Is the user correcting the agent's previous reply? (feedback signal)</summary>
    public const string Correction = "Correction";

    /// <summary>Does the reply need the completion evaluator? (promises, unsupported claims, denials)</summary>
    public const string CompletionCheck = "CompletionCheck";

    /// <summary>Is a trailing paragraph a throwaway offer, safe to strip?</summary>
    public const string TrailingOffer = "TrailingOffer";

    /// <summary>Is a new memory already fully covered by a near-duplicate?</summary>
    public const string MemoryDuplicate = "MemoryDuplicate";

    /// <summary>Does a new memory contradict an existing one?</summary>
    public const string MemoryContradiction = "MemoryContradiction";

    /// <summary>Which model tier should answer a message the keyword selector was unsure about?</summary>
    public const string TierRouting = "TierRouting";

    /// <summary>Does a reply ending in ":" or "..." really stop before delivering what it announces?</summary>
    public const string IncompleteReply = "IncompleteReply";

    /// <summary>Is a finished turn worth the follow-up evaluator's LLM call?</summary>
    public const string FollowUp = "FollowUp";

    /// <summary>Which recalled skill, if any, gets its full instructions injected?</summary>
    public const string SkillRecall = "SkillRecall";
}

/// <summary>One request: the state to judge and the named questions to answer about it.</summary>
/// <param name="Purpose">A <see cref="DecisionPurposes"/> value, for configuration, logs and metrics.</param>
/// <param name="State">Text, or an object serialized to JSON.</param>
/// <param name="Questions">The questions by answer name.</param>
public sealed record DecisionRequest(
    string Purpose, object State, IReadOnlyDictionary<string, DecisionQuestion> Questions)
{
    /// <summary>A request with one question named <paramref name="name"/>.</summary>
    public static DecisionRequest Single(string purpose, object state, string name, DecisionQuestion question) =>
        new(purpose, state, new Dictionary<string, DecisionQuestion> { [name] = question });
}

/// <summary>A bounded question.</summary>
/// <param name="Instructions">What to decide.</param>
public abstract record DecisionQuestion(string Instructions);

/// <summary>Yes or no; answered with the probability of yes.</summary>
/// <param name="Instructions">The question.</param>
/// <param name="WhenTrue">Optional: what "yes" means, for borderline cases.</param>
/// <param name="WhenFalse">Optional: what "no" means.</param>
public sealed record NoulQuestion(string Instructions, string? WhenTrue = null, string? WhenFalse = null)
    : DecisionQuestion(Instructions);

/// <summary>Exactly one of a fixed set of options; answered with the choice and a probability per option.</summary>
/// <param name="Instructions">The question.</param>
/// <param name="Options">Option label → description.</param>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Options)
    : DecisionQuestion(Instructions);

/// <summary>A place on an ordered scale; answered with the probability-weighted level index.</summary>
/// <param name="Instructions">The question.</param>
/// <param name="Levels">The levels, lowest first.</param>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels)
    : DecisionQuestion(Instructions);

/// <summary>The model's answer to one question.</summary>
/// <param name="Probability">Noul: the probability of yes.</param>
/// <param name="Choice">Choice: the selected option.</param>
/// <param name="Score">Score: the probability-weighted level index (0 = lowest level).</param>
/// <param name="Confidence">Choice/score: the model's confidence, when reported.</param>
/// <param name="Probabilities">Choice: per option label; score: per level index ("0", "1", …).</param>
public sealed record DecisionAnswer(
    double? Probability = null,
    string? Choice = null,
    double? Score = null,
    double? Confidence = null,
    IReadOnlyDictionary<string, double>? Probabilities = null);

/// <summary>The answers to one request, by question name.</summary>
/// <param name="Answers">Answer name → answer. A question the model did not answer is missing.</param>
/// <param name="Model">The model that answered, when reported.</param>
public sealed record DecisionResult(IReadOnlyDictionary<string, DecisionAnswer> Answers, string? Model = null)
{
    /// <summary>The noul probability for <paramref name="name"/>, or null.</summary>
    public double? Probability(string name) => Answers.TryGetValue(name, out var a) ? a.Probability : null;

    /// <summary>The choice for <paramref name="name"/>, or null.</summary>
    public string? Choice(string name) => Answers.TryGetValue(name, out var a) ? a.Choice : null;
}

/// <summary>The service when no model is configured: never enabled, never answers.</summary>
public sealed class NullDecisionService : IDecisionService
{
    /// <summary>Shared instance.</summary>
    public static NullDecisionService Instance { get; } = new();

    /// <inheritdoc />
    public bool IsEnabled(string purpose) => false;

    /// <inheritdoc />
    public double ThresholdFor(string purpose, double fallback) => fallback;

    /// <inheritdoc />
    public Task<DecisionResult?> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<DecisionResult?>(null);
}
