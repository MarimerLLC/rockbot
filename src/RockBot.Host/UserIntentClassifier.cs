using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// The user message behind a run that the consequential-action gate (#685) judges, plus the
/// decisions already made for it. Attached to <see cref="ActionGateScope.Request"/> by
/// <see cref="AgentLoopRunner.RunAsync"/>; subagents spawned from the run share the same instance,
/// so a change the user asked for is classified once per turn, not once per subagent.
/// </summary>
/// <param name="UserRequest">The user message the run serves.</param>
/// <param name="PreviousAgentMessage">What the agent said just before it, when known — a reply to a
/// proposal ("mark them complete now") only makes sense next to the proposal.</param>
public sealed record ActionGateRequest(string? UserRequest, string? PreviousAgentMessage)
{
    /// <summary>Decisions by effective tool name. True when the user asked for the change.</summary>
    internal ConcurrentDictionary<string, Task<bool>> Decisions { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Two requests are the same request only when they are the same instance: the decision cache is
    // part of the identity, and ActionGateScope equality must not merge two turns' caches.
    /// <inheritdoc />
    public bool Equals(ActionGateRequest? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>What the gate asks the decision model about one consequential call.</summary>
/// <param name="UserRequest">The user message the run serves.</param>
/// <param name="PreviousAgentMessage">The agent message just before it, if any.</param>
/// <param name="ToolName">The tool as reported (<see cref="ConsequentialActions.DisplayName"/>).</param>
/// <param name="Target">The system the call would change (<see cref="ConsequentialActions.DescribeTarget"/>).</param>
/// <param name="Arguments">The call's arguments, possibly truncated.</param>
public sealed record UserIntentQuery(
    string? UserRequest, string? PreviousAgentMessage, string ToolName, string Target, string? Arguments);

/// <summary>
/// A second opinion for the consequential-action gate: when the regex classifier
/// (<see cref="CompletionEvalTriggers.ClassifyUserRequest"/>) reads a user message as
/// information-only, the gate asks this classifier whether the message asked for the specific
/// change before refusing it. A verb list cannot cover every way a user asks for something;
/// a decision model can.
/// </summary>
public interface IUserIntentClassifier
{
    /// <summary>False when no model is configured; the gate then trusts the regex alone.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// The probability (0–1) that the user asked for the change, or null when the model could not
    /// answer (timeout, HTTP error, unreadable response).
    /// </summary>
    Task<double?> ProbabilityUserAskedForAsync(UserIntentQuery query, CancellationToken cancellationToken);
}

/// <summary>The classifier used when none is configured: never enabled.</summary>
public sealed class NullUserIntentClassifier : IUserIntentClassifier
{
    /// <summary>Shared instance.</summary>
    public static NullUserIntentClassifier Instance { get; } = new();

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task<double?> ProbabilityUserAskedForAsync(UserIntentQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<double?>(null);
}

/// <summary>
/// Configuration of the decision model the gate consults, under
/// <c>AgentHost:ConsequentialActionGate:DecisionModel</c>. The model is used only when
/// <see cref="Endpoint"/>, <see cref="ApiKey"/> and <see cref="Deployment"/> are all set.
/// </summary>
public sealed class DecisionModelOptions
{
    /// <summary>The Foundry resource endpoint, e.g. <c>https://my-resource.services.ai.azure.com</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The Foundry API key (sent as the <c>api-key</c> header).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The deployment name of <c>Microsoft-Decision-1</c> (or another systemone model).</summary>
    public string? Deployment { get; set; }

    /// <summary>
    /// The path of the decision API on the endpoint. Default
    /// <c>/providers/microsoft/v1/systemone</c> (Microsoft-Decision-1 on Foundry).
    /// </summary>
    public string Path { get; set; } = "/providers/microsoft/v1/systemone";

    /// <summary>
    /// The probability at or above which the user is taken to have asked for the change. Default
    /// 0.5. Tune it against labeled messages; a refused request the user really made is what
    /// broke todo completions in 0.16.14-preview.2, so err low.
    /// </summary>
    public double Threshold { get; set; } = 0.5;

    /// <summary>How long to wait for the model before treating it as failed. Default 5 seconds.</summary>
    public double TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// What the gate does when the model fails: true (default) allows the call, false refuses it as
    /// the regex alone would. The regex has already said no, so a failure here only decides who
    /// gets the benefit of the doubt.
    /// </summary>
    public bool AllowOnFailure { get; set; } = true;

    /// <summary>True when the endpoint, key and deployment are all set.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Deployment);
}

/// <summary>
/// <see cref="IUserIntentClassifier"/> backed by a systemone decision model on Microsoft Foundry
/// (<c>Microsoft-Decision-1</c>). Asks one <c>noul</c> question per call: did the user's message
/// ask for this change?
/// </summary>
public sealed class FoundryDecisionIntentClassifier : IUserIntentClassifier
{
    internal const string QuestionName = "user_asked";
    private const int MaxFieldChars = 4000;
    private const int MaxArgumentChars = 600;

    private readonly IOptions<AgentHostOptions> _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>Creates the classifier. <paramref name="httpClient"/> is for tests; by default it owns one.</summary>
    public FoundryDecisionIntentClassifier(
        IOptions<AgentHostOptions> options,
        ILogger<FoundryDecisionIntentClassifier>? logger = null,
        HttpClient? httpClient = null)
    {
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeout below
        };
    }

    private DecisionModelOptions Model => _options.Value.ConsequentialActionGate.DecisionModel;

    /// <inheritdoc />
    public bool IsEnabled => Model.IsConfigured;

    /// <inheritdoc />
    public async Task<double?> ProbabilityUserAskedForAsync(UserIntentQuery query, CancellationToken cancellationToken)
    {
        var model = Model;
        if (!model.IsConfigured) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.5, model.TimeoutSeconds)));

        var url = model.Endpoint!.TrimEnd('/') + "/" + model.Path.TrimStart('/');
        // StringContent, not JsonContent: the systemone endpoint rejects a request without
        // Content-Length (HTTP 400 no_content_length_header), and JsonContent streams without one.
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(BuildRequestBody(model.Deployment!, query).ToJsonString(),
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("api-key", model.ApiKey);

        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Decision model returned HTTP {Status} for {Tool}: {Body}",
                    (int)response.StatusCode, query.ToolName, Truncate(body, 500));
                return null;
            }

            var probability = ReadProbability(body);
            if (probability is null)
                _logger.LogWarning("Decision model response had no readable '{Question}' probability: {Body}",
                    QuestionName, Truncate(body, 1000));
            return probability;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Decision model timed out after {Seconds}s for {Tool}", model.TimeoutSeconds, query.ToolName);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Decision model call failed for {Tool}", query.ToolName);
            return null;
        }
    }

    /// <summary>The systemone request body: the conversation and proposed call as state, one noul question.</summary>
    internal static JsonObject BuildRequestBody(string deployment, UserIntentQuery query)
    {
        var state = new JsonObject
        {
            ["user_message"] = Truncate(query.UserRequest ?? "", MaxFieldChars),
        };
        if (!string.IsNullOrWhiteSpace(query.PreviousAgentMessage))
            state["agent_message_before_it"] = Truncate(query.PreviousAgentMessage, MaxFieldChars);
        state["proposed_action"] = new JsonObject
        {
            ["tool"] = query.ToolName,
            ["system_changed"] = query.Target,
            ["arguments"] = Truncate(query.Arguments ?? "", MaxArgumentChars),
        };

        return new JsonObject
        {
            ["model"] = deployment,
            ["state"] = state,
            ["questions"] = new JsonObject
            {
                [QuestionName] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] =
                        "An AI assistant is about to run proposed_action, which changes an external system. " +
                        "Did user_message ask the assistant to make this change, ask for work that requires it, " +
                        "or agree to it after agent_message_before_it proposed it?",
                    ["criteria"] = new JsonObject
                    {
                        ["true"] = "The user asked for this change, directly or indirectly (for example \"these " +
                                   "should have been marked done\"), or accepted the assistant's offer to make it",
                        ["false"] = "The user only shared information, asked a question, or asked for something " +
                                    "else; making this change would be the assistant's own idea",
                    },
                },
            },
        };
    }

    /// <summary>
    /// Reads the noul probability from a systemone response. Microsoft-Decision-1 answers
    /// <c>{"answers":{"user_asked":{"noul":0.989,"type":"noul"}}}</c> (observed 2026-10-13); a bare
    /// number and the usual field names are accepted too, in case another systemone model differs.
    /// </summary>
    internal static double? ReadProbability(string body)
    {
        var root = JsonNode.Parse(body);
        var answer = root?["answers"]?[QuestionName];
        if (answer is null) return null;
        if (TryNumber(answer, out var bare)) return Clamp(bare);
        if (answer is not JsonObject obj) return null;

        foreach (var name in (string[])["noul", "probability", "p_true", "p", "value", "score", "confidence", "true"])
        {
            if (obj[name] is { } node && TryNumber(node, out var value)) return Clamp(value);
        }
        // { "probabilities": { "true": 0.9, "false": 0.1 } }
        if (obj["probabilities"]?["true"] is { } nested && TryNumber(nested, out var p)) return Clamp(p);
        return null;
    }

    private static bool TryNumber(JsonNode node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue(out value);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
