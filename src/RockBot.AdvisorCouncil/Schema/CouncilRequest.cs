using System.Text;

namespace RockBot.AdvisorCouncil.Schema;

/// <summary>How a caller overrides one of the selector's on/off decisions.</summary>
internal enum CouncilToggle
{
    /// <summary>The selector decides.</summary>
    Auto,

    /// <summary>Always run the stage.</summary>
    On,

    /// <summary>Never run the stage.</summary>
    Off,
}

/// <summary>
/// The choice fields the council understands: the <c>advise</c> tool's enum arguments, and the
/// only things the council may ask about before it deliberates.
/// </summary>
internal static class CouncilChoices
{
    public const string TimeHorizonField = "time_horizon";
    public const string RiskAppetiteField = "risk_appetite";
    public const string ReversibilityField = "reversibility";
    public const string PreferredOptionField = "preferred_option";
    public const string DecisionKindField = "decision_kind";

    public static readonly IReadOnlyList<string> TimeHorizons = ["weeks", "months", "years", "decade_plus"];
    public static readonly IReadOnlyList<string> RiskAppetites = ["low", "moderate", "high"];
    public static readonly IReadOnlyList<string> Reversibilities = ["easily_reversed", "costly_to_reverse", "irreversible"];
    public static readonly IReadOnlyList<string> DecisionKinds = ["strategic", "technical", "personal", "organizational"];

    /// <summary>Every field the selector may report as missing, in the order a form lists them.</summary>
    public static readonly IReadOnlyList<string> AskableFields =
        [PreferredOptionField, DecisionKindField, TimeHorizonField, RiskAppetiteField, ReversibilityField];

    /// <summary>The allowed values of an enum field (not <see cref="PreferredOptionField"/>, whose values are the caller's options).</summary>
    public static IReadOnlyList<string>? ValuesOf(string field) => field switch
    {
        TimeHorizonField => TimeHorizons,
        RiskAppetiteField => RiskAppetites,
        ReversibilityField => Reversibilities,
        DecisionKindField => DecisionKinds,
        _ => null,
    };

    /// <summary>
    /// <paramref name="value"/> normalised to one of <paramref name="allowed"/> (case, spaces and
    /// hyphens ignored), or null when it isn't one.
    /// </summary>
    public static string? Normalize(string? value, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var key = value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return allowed.FirstOrDefault(a => a == key);
    }

    /// <summary>Parses an <c>auto|on|off</c> argument; anything else (including null) is <see cref="CouncilToggle.Auto"/>.</summary>
    public static CouncilToggle ParseToggle(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" => CouncilToggle.On,
        "off" or "false" or "no" => CouncilToggle.Off,
        _ => CouncilToggle.Auto,
    };
}

/// <summary>
/// One request to the council: the question plus whatever the caller (or the council's one
/// clarifying question) supplied about it. Every field but <see cref="Question"/> is optional.
/// </summary>
internal sealed record CouncilRequest(string Question)
{
    /// <summary>Free text from the caller: goals, constraints, stakeholders, what has been tried.</summary>
    public string? Context { get; init; }

    /// <summary>The options being weighed, when there are named options.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>One of <see cref="CouncilChoices.TimeHorizons"/>.</summary>
    public string? TimeHorizon { get; init; }

    /// <summary>One of <see cref="CouncilChoices.RiskAppetites"/>.</summary>
    public string? RiskAppetite { get; init; }

    /// <summary>One of <see cref="CouncilChoices.Reversibilities"/>.</summary>
    public string? Reversibility { get; init; }

    /// <summary>One of <see cref="CouncilChoices.DecisionKinds"/>; only ever set by the clarifying question.</summary>
    public string? DecisionKind { get; init; }

    /// <summary>The option the user leans toward (one of <see cref="Options"/>); only ever set by the clarifying question.</summary>
    public string? PreferredOption { get; init; }

    /// <summary>Persona ids the caller forces; empty lets the selector choose.</summary>
    public IReadOnlyList<string> Personas { get; init; } = [];

    /// <summary>Overrides the selector's critique decision.</summary>
    public CouncilToggle Critique { get; init; }

    /// <summary>Overrides the selector's pre-research decision.</summary>
    public CouncilToggle PreResearch { get; init; }

    /// <summary>
    /// Fields the selector said would change the deliberation but nobody supplied (the question
    /// wasn't asked, or was declined). Synthesis states what it assumed for each.
    /// </summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    /// <summary>What happened to the clarifying question: <c>not_asked</c>, <c>answered</c> or <c>declined</c>.</summary>
    public string Clarification { get; init; } = ClarificationStatus.NotAsked;

    /// <summary>True when the caller already supplied <paramref name="field"/>.</summary>
    public bool Has(string field) => field switch
    {
        CouncilChoices.TimeHorizonField => TimeHorizon is not null,
        CouncilChoices.RiskAppetiteField => RiskAppetite is not null,
        CouncilChoices.ReversibilityField => Reversibility is not null,
        CouncilChoices.DecisionKindField => DecisionKind is not null,
        CouncilChoices.PreferredOptionField => PreferredOption is not null,
        _ => false,
    };

    /// <summary>
    /// A copy with the arguments cleaned up: trimmed text, blank entries dropped, enum values
    /// normalised (an unknown value is dropped rather than failing the call).
    /// </summary>
    public static CouncilRequest Create(
        string question,
        string? context = null,
        IEnumerable<string>? options = null,
        string? timeHorizon = null,
        string? riskAppetite = null,
        string? reversibility = null,
        IEnumerable<string>? personas = null,
        string? critique = null,
        string? preResearch = null) => new(question.Trim())
    {
        Context = string.IsNullOrWhiteSpace(context) ? null : context.Trim(),
        Options = Clean(options),
        TimeHorizon = CouncilChoices.Normalize(timeHorizon, CouncilChoices.TimeHorizons),
        RiskAppetite = CouncilChoices.Normalize(riskAppetite, CouncilChoices.RiskAppetites),
        Reversibility = CouncilChoices.Normalize(reversibility, CouncilChoices.Reversibilities),
        Personas = [.. Clean(personas).Select(p => p.ToLowerInvariant())],
        Critique = CouncilChoices.ParseToggle(critique),
        PreResearch = CouncilChoices.ParseToggle(preResearch),
    };

    private static IReadOnlyList<string> Clean(IEnumerable<string>? values) =>
        values is null ? [] : [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>True when there is anything beyond the question to tell a step.</summary>
    public bool HasBrief =>
        Context is not null || Options.Count > 0 || TimeHorizon is not null || RiskAppetite is not null
        || Reversibility is not null || DecisionKind is not null || PreferredOption is not null;

    /// <summary>
    /// The question followed by a "Decision brief" with what is known about it. Every step puts
    /// this in its prompt where it used to put the bare question.
    /// </summary>
    public string RenderBrief()
    {
        if (!HasBrief)
            return Question;

        var sb = new StringBuilder(Question).AppendLine().AppendLine();
        sb.AppendLine("Decision brief (from the person asking):");
        if (Context is not null)
            sb.Append("- Context: ").AppendLine(Context);
        if (Options.Count > 0)
            sb.Append("- Options being weighed: ").AppendLine(string.Join("; ", Options));
        if (PreferredOption is not null)
            sb.Append("- Currently leaning toward: ").AppendLine(PreferredOption);
        if (DecisionKind is not null)
            sb.Append("- Kind of decision: ").AppendLine(DecisionKind);
        if (TimeHorizon is not null)
            sb.Append("- Time horizon: ").AppendLine(TimeHorizon.Replace('_', ' '));
        if (RiskAppetite is not null)
            sb.Append("- Risk appetite: ").AppendLine(RiskAppetite);
        if (Reversibility is not null)
            sb.Append("- Reversibility: ").AppendLine(Reversibility.Replace('_', ' '));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Values of <see cref="CouncilRequest.Clarification"/>.</summary>
internal static class ClarificationStatus
{
    public const string NotAsked = "not_asked";
    public const string Answered = "answered";
    public const string Declined = "declined";
}
