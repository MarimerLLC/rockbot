using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.ResearchAgent;

/// <summary>One reading of an ambiguous question.</summary>
/// <param name="Id">Short stable identifier (the elicitation's enum value).</param>
/// <param name="Label">What the reading means, as shown to whoever answers.</param>
internal sealed record ResearchInterpretation(string Id, string Label);

/// <summary>
/// The scoping pass's verdict: whether the question needs a clarifying question, the readings to
/// offer, and the one to research if nobody picks.
/// </summary>
internal sealed record ResearchScope(IReadOnlyList<ResearchInterpretation> Interpretations, string? BestId)
{
    /// <summary>Not ambiguous: research the question as asked.</summary>
    public static ResearchScope Clear { get; } = new([], null);

    /// <summary>True when there are at least two readings to choose between.</summary>
    public bool IsAmbiguous => Interpretations.Count >= 2;

    /// <summary>The reading to research when nobody picks one.</summary>
    public ResearchInterpretation? Best =>
        Interpretations.FirstOrDefault(i => i.Id == BestId) ?? Interpretations.FirstOrDefault();

    /// <summary>The reading with <paramref name="id"/>, or null.</summary>
    public ResearchInterpretation? Find(string? id) =>
        id is null ? null : Interpretations.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
}

/// <summary>Decides, before any search budget is spent, whether a research question is ambiguous.</summary>
internal interface IResearchScoper
{
    Task<ResearchScope> ScopeAsync(ResearchRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The cheap scoping pass: one Balanced-tier LLM call, no tools, over the question, context and
/// constraints. Anything other than a clear "ambiguous, here are the readings" verdict means the
/// question is researched as asked. A scoping failure must never block research.
/// </summary>
/// <remarks>
/// This is a one-shot classification with no tool calls, like the other classifiers that call
/// <see cref="ILlmClient"/> directly. The research itself goes through <see cref="AgentLoopRunner"/>.
/// </remarks>
internal sealed class ResearchScoper(ILlmClient llmClient, ILogger<ResearchScoper> logger) : IResearchScoper
{
    /// <summary>Most readings offered in one question.</summary>
    internal const int MaxInterpretations = 5;

    internal const string Prompt =
        """
        You scope research questions before any research starts. Decide whether the question is
        genuinely ambiguous in a way that would send the research in clearly different directions
        (for example "Mercury" could be the planet, the element or the band), taking the context
        and constraints into account. A question that is broad, open-ended or underspecified is NOT
        ambiguous: it can be researched as asked. Only flag ambiguity when the context does not
        already settle which meaning is intended.

        Reply with JSON only, no prose and no code fences:
        {"ambiguous": true|false,
         "interpretations": [{"id": "short-kebab-id", "label": "one-line description"}],
         "best": "id of the most likely interpretation"}

        When not ambiguous, reply {"ambiguous": false, "interpretations": [], "best": null}.
        When ambiguous, give 2 to 5 interpretations.
        """;

    public async Task<ResearchScope> ScopeAsync(ResearchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, Prompt),
                new(ChatRole.User, ResearchRunner.BuildUserMessage(request with { Interpretation = null })),
            };
            var response = await llmClient.GetResponseAsync(messages, ModelTier.Balanced, new ChatOptions(), cancellationToken);
            var scope = Parse(response.Text);
            logger.LogInformation("Research scoping: {Verdict} ({Count} interpretation(s))",
                scope.IsAmbiguous ? "ambiguous" : "clear", scope.Interpretations.Count);
            return scope;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Research scoping failed; researching the question as asked");
            return ResearchScope.Clear;
        }
    }

    /// <summary>Reads the scoping reply; anything malformed is <see cref="ResearchScope.Clear"/>.</summary>
    internal static ResearchScope Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ResearchScope.Clear;

        // Tolerate code fences or a sentence around the JSON object.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return ResearchScope.Clear;

        Reply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<Reply>(text[start..(end + 1)], ReplyOptions);
        }
        catch (JsonException)
        {
            return ResearchScope.Clear;
        }

        if (reply is not { Ambiguous: true, Interpretations: { } raw })
            return ResearchScope.Clear;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var interpretations = new List<ResearchInterpretation>();
        foreach (var item in raw)
        {
            var label = item?.Label?.Trim();
            if (string.IsNullOrEmpty(label))
                continue;
            var id = string.IsNullOrWhiteSpace(item!.Id) ? $"option-{interpretations.Count + 1}" : item.Id.Trim();
            if (!seen.Add(id))
                continue;
            interpretations.Add(new ResearchInterpretation(id, label));
            if (interpretations.Count == MaxInterpretations)
                break;
        }

        if (interpretations.Count < 2)
            return ResearchScope.Clear;

        var best = interpretations.Any(i => i.Id == reply.Best) ? reply.Best : interpretations[0].Id;
        return new ResearchScope(interpretations, best);
    }

    private static readonly JsonSerializerOptions ReplyOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed class Reply
    {
        public bool Ambiguous { get; set; }
        public List<ReplyItem?>? Interpretations { get; set; }
        public string? Best { get; set; }
    }

    private sealed class ReplyItem
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
    }
}
