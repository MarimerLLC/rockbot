using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// A scripted <see cref="IDecisionService"/>: answers every noul question with <c>probability</c>
/// (or per question name from <c>byQuestion</c>), every choice question with <c>choice</c>, and
/// records the requests. A null probability makes the call fail (returns null).
/// </summary>
internal sealed class FakeDecisionService(
    double? probability = 0.99,
    string? choice = null,
    IReadOnlyDictionary<string, double>? byQuestion = null) : IDecisionService
{
    public bool Enabled { get; init; } = true;
    public HashSet<string> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Thresholds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DecisionRequest> Requests { get; } = [];

    public bool IsEnabled(string purpose) => Enabled && !Disabled.Contains(purpose);

    public double ThresholdFor(string purpose, double fallback) =>
        Thresholds.TryGetValue(purpose, out var t) ? t : fallback;

    public Task<DecisionResult?> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        lock (Requests) Requests.Add(request);
        if (probability is null && byQuestion is null && choice is null)
            return Task.FromResult<DecisionResult?>(null);

        var answers = new Dictionary<string, DecisionAnswer>();
        foreach (var (name, question) in request.Questions)
        {
            answers[name] = question switch
            {
                ChoiceQuestion => new DecisionAnswer(Choice: choice, Confidence: 0.9),
                _ when byQuestion is not null && byQuestion.TryGetValue(name, out var p) => new DecisionAnswer(Probability: p),
                _ => new DecisionAnswer(Probability: probability),
            };
        }
        return Task.FromResult<DecisionResult?>(new DecisionResult(answers, "fake"));
    }
}
