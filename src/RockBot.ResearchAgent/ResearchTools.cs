using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RockBot.ResearchAgent;

/// <summary>
/// The research server's one MCP tool, <c>research</c>: question in, synthesised answer and the
/// sources it read out.
/// </summary>
/// <remarks>
/// <para>
/// Before any search budget is spent, a cheap scoping pass (<see cref="IResearchScoper"/>) decides
/// whether the question is genuinely ambiguous. If it is, the tool asks exactly one question: a
/// single choice between the candidate readings, with no free-text field. It never asks
/// mid-research, and never for credentials or confirmations. A declined, cancelled or invalid
/// answer is not a failure: research follows the most likely reading and says so at the top.
/// </para>
/// <para>
/// How it asks depends on how the call runs (<see cref="ResearchCallMode"/>). As a task (the
/// bridge's background tasks, <c>CallToolWithPollingAsync</c>) it calls <c>ElicitAsync</c>, which
/// the Tasks extension routes through the task store. As a plain call it throws
/// <see cref="InputRequiredException"/> (MRTR), carrying the scoping result in a protected
/// <c>requestState</c> (<see cref="ResearchScopeToken"/>), so the retried call skips scoping. A
/// retried call never asks again, so the round trip always ends.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class ResearchTools(
    IResearchRunner runner,
    IResearchScoper scoper,
    ResearchScopeToken scopeToken,
    ILogger<ResearchTools> logger)
{
    /// <summary>The tool's name.</summary>
    public const string ToolName = "research";

    /// <summary>The elicitation's input key, and its single field.</summary>
    internal const string AnswerKey = "interpretation";

    /// <summary>Builds the <c>research</c> tool bound to this instance (for hosts without DI activation).</summary>
    public McpServerTool CreateTool() => McpServerTool.Create(
        typeof(ResearchTools).GetMethod(nameof(ResearchAsync), BindingFlags.Public | BindingFlags.Instance)!,
        this);

    [McpServerTool(Name = ToolName, Title = "Research", ReadOnly = true, OpenWorld = true)]
    [Description(
        "Researches a question on the web: searches, reads several sources, and returns a " +
        "synthesised answer followed by the list of sources it read. Runs take minutes. If the " +
        "question is ambiguous (e.g. \"Mercury\": planet, element or band?) it may first ask " +
        "once which reading you mean; otherwise it never asks. Put background you already have " +
        "in context, and scope limits in constraints.")]
    public async Task<CallToolResult> ResearchAsync(
        RequestContext<CallToolRequestParams> request,
        [Description("What to research.")] string question,
        [Description("Optional background: who is asking, why, what is already known. Helps settle what an ambiguous question means.")]
        string? context = null,
        [Description("Optional scope limits: time range, region, sources to prefer or avoid, depth.")]
        string? constraints = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "research needs a question." }],
            };
        }

        var research = new ResearchRequest(
            question.Trim(),
            string.IsNullOrWhiteSpace(context) ? null : context.Trim(),
            string.IsNullOrWhiteSpace(constraints) ? null : constraints.Trim());

        var runsAsTask = ResearchCallMode.IsTask(request);
        var resolved = await ResolveInterpretationAsync(request, research, runsAsTask, cancellationToken);

        var runId = "mcp-" + Guid.NewGuid().ToString("N")[..12];
        logger.LogInformation(
            "research {RunId} ({Mode}): {Question}{Interpretation}",
            runId, runsAsTask ? "task" : "call", research.Question,
            resolved is null ? "" : $" — as \"{resolved.Interpretation.Label}\" ({(resolved.Chosen ? "chosen" : "assumed")})");

        var outcome = await runner.RunAsync(
            research with { Interpretation = resolved?.Interpretation.Label },
            runId, onProgress: null, cancellationToken);

        return BuildResult(outcome, resolved);
    }

    /// <summary>The reading the research follows and whether the caller picked it, or null when the question was clear.</summary>
    internal sealed record Resolved(ResearchInterpretation Interpretation, bool Chosen, string? Action);

    private async Task<Resolved?> ResolveInterpretationAsync(
        RequestContext<CallToolRequestParams> request, ResearchRequest research, bool runsAsTask, CancellationToken ct)
    {
        var state = request.Params?.RequestState;

        // The retried MRTR call: the answer comes with the scope we sealed when asking.
        if (!runsAsTask && state is not null)
        {
            var sealedScope = scopeToken.Unprotect(state, research);
            if (sealedScope is not null)
                return Resolve(sealedScope, ReadAnswer(request.Params?.InputResponses));

            logger.LogInformation("research: the request state was invalid or expired; scoping again without asking");
        }

        var scope = await scoper.ScopeAsync(research, ct);
        if (!scope.IsAmbiguous)
            return null;

        if (!ResearchCallMode.ClientCanElicit(request))
        {
            logger.LogInformation("research: the client can't answer a question; using the most likely reading");
            return Resolve(scope, answer: null);
        }

        var question = BuildQuestion(research, scope);

        if (runsAsTask)
        {
            ElicitResult? answer = null;
            try
            {
#pragma warning disable MCP9005 // inside a task, ElicitAsync goes through the task store, not a session
                answer = await request.Server.ElicitAsync(question, ct);
#pragma warning restore MCP9005
            }
            catch (Exception ex) when (ex is InvalidOperationException or McpException && !ct.IsCancellationRequested)
            {
                logger.LogInformation(ex, "research: the clarifying question could not be asked; using the most likely reading");
            }
            return Resolve(scope, answer);
        }

        // A plain call asks once, through MRTR. A retried call (any request state) never asks
        // again, and a client without MRTR gets the most likely reading.
        if (state is null && request.Server.IsMrtrSupported)
        {
            throw new InputRequiredException(
                new Dictionary<string, InputRequest> { [AnswerKey] = InputRequest.ForElicitation(question) },
                scopeToken.Protect(research, scope));
        }

        return Resolve(scope, answer: null);
    }

    /// <summary>The chosen reading on an accepted, valid answer; otherwise the most likely one.</summary>
    internal static Resolved Resolve(ResearchScope scope, ElicitResult? answer)
    {
        if (answer is { Action: "accept" }
            && answer.Content?.TryGetValue(AnswerKey, out var value) == true
            && value.ValueKind == JsonValueKind.String
            && scope.Find(value.GetString()) is { } chosen)
        {
            return new Resolved(chosen, Chosen: true, answer.Action);
        }

        return new Resolved(scope.Best!, Chosen: false, answer?.Action);
    }

    private static ElicitResult? ReadAnswer(IDictionary<string, InputResponse>? responses)
    {
        if (responses is null || !responses.TryGetValue(AnswerKey, out var response))
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

    /// <summary>The one clarifying question: a single required choice between the readings.</summary>
    internal static ElicitRequestParams BuildQuestion(ResearchRequest research, ResearchScope scope) => new()
    {
        Message = $"\"{Shorten(research.Question, 200)}\" could mean different things. Which should I research?",
        RequestedSchema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                [AnswerKey] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                {
                    Title = "Meaning",
                    // Clients that list only the enum values (the bridge's hand-back note does)
                    // still see what each one means.
                    Description = "The reading of the question to research: " +
                        string.Join("; ", scope.Interpretations.Select(i => $"{i.Id} = {i.Label}")) + ".",
                    OneOf = [.. scope.Interpretations.Select(i => new ElicitRequestParams.EnumSchemaOption
                    {
                        Const = i.Id,
                        Title = i.Label,
                    })],
                },
            },
            Required = [AnswerKey],
        },
    };

    internal static CallToolResult BuildResult(ResearchOutcome outcome, Resolved? resolved)
    {
        var answer = new StringBuilder();
        if (resolved is not null)
        {
            answer.AppendLine(resolved.Chosen
                ? $"Researched as: {resolved.Interpretation.Label}"
                : $"Interpreted as: {resolved.Interpretation.Label} (the question was ambiguous and no reading was chosen, so this is the most likely one)");
            answer.AppendLine();
        }
        answer.Append(outcome.Answer);

        var content = new List<ContentBlock> { new TextContentBlock { Text = answer.ToString() } };
        if (outcome.Sources.Count > 0)
        {
            var list = new StringBuilder("Sources consulted:");
            foreach (var source in outcome.Sources)
            {
                list.AppendLine();
                list.Append("- ").Append(source.Title is null ? source.Url : $"{source.Title} — {source.Url}");
            }
            content.Add(new TextContentBlock { Text = list.ToString() });
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(new StructuredResult
            {
                Answer = outcome.Answer,
                Interpretation = resolved?.Interpretation.Label,
                InterpretationChosen = resolved?.Chosen,
                Sources = [.. outcome.Sources.Select(s => new StructuredSource { Url = s.Url, Title = s.Title })],
            }, StructuredJson),
        };
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static readonly JsonSerializerOptions StructuredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class StructuredResult
    {
        public string Answer { get; init; } = "";
        public string? Interpretation { get; init; }
        public bool? InterpretationChosen { get; init; }
        public List<StructuredSource> Sources { get; init; } = [];
    }

    private sealed class StructuredSource
    {
        public string Url { get; init; } = "";
        public string? Title { get; init; }
    }
}
