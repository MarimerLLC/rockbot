using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil;

/// <summary>
/// The council's one MCP tool, <c>advise</c>: a question and what the caller knows about it in,
/// multi-perspective guidance with its tensions and assumptions out.
/// </summary>
/// <remarks>
/// <para>
/// Only the Select step may ask a question (<c>design/advisor-council-mcp.md</c>, "Clarifying
/// questions"): at most once, before any persona or research runs, and only as choice fields
/// (<see cref="CouncilChoices"/>). Nothing research reads from the web can therefore become a
/// question. A declined or unanswered field becomes a stated assumption. When the question is
/// too thin to deliberate on at all, the tool returns early and names the <c>context</c> it
/// needs: free text is never elicited.
/// </para>
/// <para>
/// How it asks depends on how the call runs (<see cref="CouncilCallMode"/>), as for the research
/// server: inside a task through <c>ElicitAsync</c>; as a plain call through MRTR
/// (<see cref="InputRequiredException"/>) with Select's output sealed in <c>requestState</c>
/// (<see cref="CouncilClarificationToken"/>). A retried call never asks again.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class CouncilTools(
    ICouncil council,
    CouncilClarificationToken clarificationToken,
    IOptions<CouncilOptions> councilOptions,
    ILogger<CouncilTools> logger)
{
    /// <summary>The tool's name.</summary>
    public const string ToolName = "advise";

    /// <summary>The MRTR input key of the clarifying question.</summary>
    internal const string QuestionKey = "clarification";

    /// <summary>Builds the <c>advise</c> tool bound to this instance (for hosts without DI activation).</summary>
    public McpServerTool CreateTool() => McpServerTool.Create(
        typeof(CouncilTools).GetMethod(nameof(AdviseAsync), BindingFlags.Public | BindingFlags.Instance)!,
        this);

#pragma warning disable IDE1006 // snake_case parameter names are the tool's argument names
    [McpServerTool(Name = ToolName, Title = "Advisor council", ReadOnly = true, OpenWorld = true)]
    [Description(
        "Convenes an advisor council on a consequential or contested decision: several personas " +
        "(skeptic, engineer, economist, ethicist, long-term thinker, user advocate) examine it, " +
        "may research it, critique each other, and a synthesis names the tensions, gives an " +
        "integrated recommendation and lists the assumptions it made. Runs take one to three minutes. " +
        "Fill context, options and the choice arguments from what the person has already said: the " +
        "council asks at most one multiple-choice question about what is still open, and anything " +
        "left open becomes a stated assumption. If the question is too thin to deliberate on, it " +
        "returns early (status needs_context) naming what context to add; gather that and call again.")]
    public async Task<CallToolResult> AdviseAsync(
        RequestContext<CallToolRequestParams> request,
        [Description("The decision or idea to deliberate on.")] string question,
        [Description("Optional free text: goals, success criteria, constraints, stakeholders, what has been tried, what the person already believes.")]
        string? context = null,
        [Description("Optional: the options being weighed, when there are named options.")]
        string[]? options = null,
        [Description("Optional: how far ahead the decision matters: weeks, months, years or decade_plus.")]
        string? time_horizon = null,
        [Description("Optional: how much downside the person can accept: low, moderate or high.")]
        string? risk_appetite = null,
        [Description("Optional: easily_reversed, costly_to_reverse or irreversible.")]
        string? reversibility = null,
        [Description("Optional: force the persona set by id (skeptic, engineer, economist, ethicist, long_term, user_advocate). Usually leave empty.")]
        string[]? personas = null,
        [Description("Optional: auto (default), on or off. Whether personas critique each other's views.")]
        string? critique = null,
        [Description("Optional: auto (default), on or off. Whether the council researches shared facts before the personas start.")]
        string? pre_research = null,
        CancellationToken cancellationToken = default)
#pragma warning restore IDE1006
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "advise needs a question." }],
            };
        }

        var advice = CouncilRequest.Create(
            question, context, options, time_horizon, risk_appetite, reversibility, personas, critique, pre_research);

        var runsAsTask = CouncilCallMode.IsTask(request);
        var state = request.Params?.RequestState;

        // ── Select (or, on the retried MRTR call, the selection we sealed when asking) ──
        SelectorOutput? selection = null;
        ElicitResult? answer = null;
        var asked = false;
        if (!runsAsTask && state is not null)
        {
            selection = clarificationToken.Unprotect(state, advice);
            if (selection is not null)
            {
                asked = true;
                answer = ReadAnswer(request.Params?.InputResponses);
            }
            else
            {
                logger.LogInformation("advise: the request state was invalid or expired; selecting again without asking");
            }
        }

        selection ??= await council.SelectAsync(advice, cancellationToken);

        if (selection.TooThin)
        {
            logger.LogInformation("advise: too thin to deliberate on; asking the caller for context ({Needs})",
                string.Join("; ", selection.Needs ?? []));
            return BuildNeedsContext(advice, selection);
        }

        // ── The one clarifying question ──
        var missing = selection.Missing ?? [];
        if (!asked && missing.Count > 0)
        {
            if (!CouncilCallMode.ClientCanElicit(request))
            {
                logger.LogInformation("advise: the client can't answer a question; assuming [{Missing}]", string.Join(",", missing));
            }
            else
            {
                var form = BuildQuestion(advice, missing);
                if (runsAsTask)
                {
                    try
                    {
#pragma warning disable MCP9005 // inside a task, ElicitAsync goes through the task store, not a session
                        answer = await request.Server.ElicitAsync(form, cancellationToken);
#pragma warning restore MCP9005
                        asked = true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or McpException && !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogInformation(ex, "advise: the clarifying question could not be asked; assuming instead");
                    }
                }
                else if (state is null && request.Server.IsMrtrSupported)
                {
                    // A plain call asks once, through MRTR. A retried call (any request state)
                    // never asks again, and a client without MRTR gets assumptions.
                    throw new InputRequiredException(
                        new Dictionary<string, InputRequest> { [QuestionKey] = InputRequest.ForElicitation(form) },
                        clarificationToken.Protect(advice, selection));
                }
            }
        }

        var resolved = Resolve(advice, missing, answer, asked);

        // ── Deliberate ──
        var runId = "council-" + Guid.NewGuid().ToString("N")[..12];
        logger.LogInformation(
            "advise {RunId} ({Mode}, clarification={Clarification}, unresolved=[{Unresolved}]): {Question}",
            runId, runsAsTask ? "task" : "call", resolved.Clarification, string.Join(",", resolved.Unresolved),
            resolved.Question.Length > 300 ? resolved.Question[..300] + "…" : resolved.Question);

        var overallTimeout = TimeSpan.FromSeconds(Math.Max(30, councilOptions.Value.OverallTimeoutSeconds));
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(overallTimeout);

        CouncilResponse response;
        try
        {
            response = await council.DeliberateAsync(resolved, selection, runId, overallCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("advise {RunId} hit the overall timeout ({Sec}s)", runId, overallTimeout.TotalSeconds);
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"Council deliberation timed out after {overallTimeout.TotalSeconds:0} s." }],
            };
        }

        return BuildResult(response);
    }

    /// <summary>
    /// <paramref name="advice"/> with the accepted answers applied, the still-open fields in
    /// <see cref="CouncilRequest.Unresolved"/>, and what happened to the question.
    /// </summary>
    internal static CouncilRequest Resolve(CouncilRequest advice, IReadOnlyList<string> missing, ElicitResult? answer, bool asked)
    {
        var resolved = advice;
        var unresolved = new List<string>();
        var answered = 0;

        foreach (var field in missing)
        {
            var value = answer is { Action: "accept" } ? ReadString(answer, field) : null;
            var applied = field switch
            {
                CouncilChoices.PreferredOptionField => OptionAt(advice, value) is { } option
                    ? resolved with { PreferredOption = option } : null,
                CouncilChoices.TimeHorizonField => CouncilChoices.Normalize(value, CouncilChoices.TimeHorizons) is { } v
                    ? resolved with { TimeHorizon = v } : null,
                CouncilChoices.RiskAppetiteField => CouncilChoices.Normalize(value, CouncilChoices.RiskAppetites) is { } v
                    ? resolved with { RiskAppetite = v } : null,
                CouncilChoices.ReversibilityField => CouncilChoices.Normalize(value, CouncilChoices.Reversibilities) is { } v
                    ? resolved with { Reversibility = v } : null,
                CouncilChoices.DecisionKindField => CouncilChoices.Normalize(value, CouncilChoices.DecisionKinds) is { } v
                    ? resolved with { DecisionKind = v } : null,
                _ => null,
            };

            if (applied is null)
            {
                unresolved.Add(field);
            }
            else
            {
                resolved = applied;
                answered++;
            }
        }

        return resolved with
        {
            Unresolved = unresolved,
            Clarification = !asked ? ClarificationStatus.NotAsked
                : answered > 0 ? ClarificationStatus.Answered
                : ClarificationStatus.Declined,
        };
    }

    private static string? ReadString(ElicitResult answer, string field) =>
        answer.Content?.TryGetValue(field, out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The caller's option a <c>preferred_option</c> answer names (<c>option_1</c>, …), or null.</summary>
    private static string? OptionAt(CouncilRequest advice, string? value)
    {
        if (value is null || !value.StartsWith("option_", StringComparison.Ordinal)
            || !int.TryParse(value["option_".Length..], out var n) || n < 1 || n > advice.Options.Count)
        {
            return null;
        }
        return advice.Options[n - 1];
    }

    private static ElicitResult? ReadAnswer(IDictionary<string, InputResponse>? responses)
    {
        if (responses is null || !responses.TryGetValue(QuestionKey, out var response))
            return null;
        try
        {
            return response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one clarifying question: a single form with a choice field per missing fact, none
    /// required (an unanswered field becomes an assumption), no free text, no yes/no.
    /// </summary>
    internal static ElicitRequestParams BuildQuestion(CouncilRequest advice, IReadOnlyList<string> missing)
    {
        var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();
        foreach (var field in CouncilChoices.AskableFields.Where(missing.Contains))
        {
            if (field == CouncilChoices.PreferredOptionField)
            {
                if (advice.Options.Count < 2)
                    continue;
                properties[field] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                {
                    Title = "Leaning toward",
                    Description = "Which of the options the person currently leans toward.",
                    OneOf = [.. advice.Options.Select((o, i) => new ElicitRequestParams.EnumSchemaOption
                    {
                        Const = $"option_{i + 1}",
                        Title = Shorten(o, 100),
                    })],
                };
                continue;
            }

            var (title, description) = field switch
            {
                CouncilChoices.DecisionKindField => ("Kind of decision", "What kind of decision this is."),
                CouncilChoices.TimeHorizonField => ("Time horizon", "How far ahead the decision matters."),
                CouncilChoices.RiskAppetiteField => ("Risk appetite", "How much downside the person can accept."),
                CouncilChoices.ReversibilityField => ("Reversibility", "How hard the decision would be to undo."),
                _ => (field, field),
            };
            properties[field] = new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Title = title,
                Description = description,
                OneOf = [.. CouncilChoices.ValuesOf(field)!.Select(v => new ElicitRequestParams.EnumSchemaOption
                {
                    Const = v,
                    Title = Humanize(v),
                })],
            };
        }

        return new ElicitRequestParams
        {
            Message =
                $"Before the advisor council deliberates on \"{Shorten(advice.Question, 200)}\": these would change its advice. " +
                "Answer what is known; anything left blank becomes a stated assumption.",
            RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = properties },
        };
    }

    /// <summary>The early return for a question too thin to deliberate on: what <c>context</c> should cover.</summary>
    internal static CallToolResult BuildNeedsContext(CouncilRequest advice, SelectorOutput selection)
    {
        var needs = selection.Needs is { Count: > 0 } n ? n : ["what is being decided, and the goal behind it"];
        var openChoices = new[] { CouncilChoices.TimeHorizonField, CouncilChoices.RiskAppetiteField, CouncilChoices.ReversibilityField }
            .Where(f => !advice.Has(f))
            .ToList();

        var text = new StringBuilder("The council needs more to go on before it can deliberate on this question; nothing has run yet. ");
        text.AppendLine("Call advise again with `context` covering:");
        foreach (var need in needs)
            text.Append("- ").AppendLine(need);
        if (openChoices.Count > 0)
            text.Append("Also set ").Append(string.Join(", ", openChoices.Select(c => $"`{c}`"))).AppendLine(" if they are known.");
        text.Append("Take these from the conversation, or ask the person.");

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text.ToString() }],
            StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["status"] = "needs_context",
                ["question"] = advice.Question,
                ["needs"] = needs,
                ["open_choices"] = openChoices,
            }),
        };
    }

    /// <summary>
    /// The result: the synthesis prose (with a coverage banner when personas are missing, and the
    /// assumptions after it) as text, and the full response as structured content.
    /// </summary>
    internal static CallToolResult BuildResult(CouncilResponse response) => new()
    {
        Content = [new TextContentBlock { Text = BuildText(response) }],
        StructuredContent = JsonSerializer.SerializeToElement(response),
    };

    internal static string BuildText(CouncilResponse response)
    {
        var sb = new StringBuilder();

        // The caller would otherwise have no way of knowing the recommendation is missing perspectives.
        var missing = response.Personas.Where(p => p.Status != PersonaStatus.Ok).ToList();
        if (missing.Count > 0)
        {
            var contributed = response.Personas.Count - missing.Count;
            sb.Append("> **Council coverage:** ").Append(contributed).Append(" of ").Append(response.Personas.Count)
              .Append(" personas contributed. Missing perspectives: ")
              .Append(string.Join(", ", missing.Select(p => $"{p.Id} ({p.Status})")))
              .AppendLine(". The integrated recommendation below does not reflect those viewpoints.")
              .AppendLine().AppendLine("---").AppendLine();
        }

        sb.Append(response.Synthesis.TrimEnd());

        if (response.Assumptions is { Count: > 0 } assumptions)
        {
            sb.AppendLine().AppendLine().AppendLine("**Assumptions** (the advice depends on these; confirm the load-bearing ones):");
            foreach (var a in assumptions)
                sb.Append("- ").AppendLine(a);
        }

        sb.AppendLine().AppendLine().Append(BuildRunSummary(response));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// One line on how the council ran: who contributed, whether pre-research and critique ran,
    /// confidence, the clarifying question and the duration. The structured content has all of it,
    /// but a caller that reads only the text (the MCP bridge hands the agent the text) otherwise
    /// can't say how broad the review was.
    /// </summary>
    internal static string BuildRunSummary(CouncilResponse response)
    {
        var m = response.Metadata;
        var contributed = response.Personas.Where(p => p.Status == PersonaStatus.Ok).Select(p => p.Id).ToList();
        var critique = m.CritiqueRun ? "ran"
            : m.CritiqueSkipped ? "skipped (the views already agreed)"
            : "not run";

        var personas = contributed.Count == 0
            ? $"0 of {response.Personas.Count} personas contributed"
            : $"{contributed.Count} of {response.Personas.Count} personas contributed ({string.Join(", ", contributed)})";

        return $"**Council run:** {personas} · pre-research: {(m.PreResearchRun ? "ran" : "not run")} · " +
               $"critique: {critique} · confidence: {response.Confidence} · " +
               $"clarifying question: {m.Clarification.Replace('_', ' ')} · {m.DurationMs / 1000.0:0} s";
    }

    private static string Humanize(string value) =>
        value == "decade_plus" ? "A decade or more" : char.ToUpperInvariant(value[0]) + value[1..].Replace('_', ' ');

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
