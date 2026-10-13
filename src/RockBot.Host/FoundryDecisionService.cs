using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Configuration of the shared decision model (<see cref="IDecisionService"/>), under the top-level
/// <c>DecisionModel</c> section (env: <c>DecisionModel__Endpoint</c>, …). The model is used only when
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

    /// <summary>The decision API path on the endpoint. Default <c>/providers/microsoft/v1/systemone</c>.</summary>
    public string Path { get; set; } = "/providers/microsoft/v1/systemone";

    /// <summary>How long to wait for an answer before the caller keeps its deterministic one. Default 5 seconds.</summary>
    public double TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Per-purpose switches and thresholds, keyed by <see cref="DecisionPurposes"/> value
    /// (env: <c>DecisionModel__Uses__Correction__Enabled=false</c>). A purpose with no entry is enabled
    /// with its caller's default threshold.
    /// </summary>
    public Dictionary<string, DecisionUseOptions> Uses { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default for <see cref="TierRoutingMargin"/>.</summary>
    public const double DefaultTierRoutingMargin = 0.08;

    /// <summary>
    /// Tier routing asks the model only when the keyword score is within this distance of a tier
    /// ceiling (ceilings default to 0.25 and 0.55). Default 0.08. 0 effectively turns it off.
    /// </summary>
    public double TierRoutingMargin { get; set; } = DefaultTierRoutingMargin;

    /// <summary>True when the endpoint, key and deployment are all set.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Deployment);
}

/// <summary>Settings for one <see cref="DecisionPurposes"/> use of the decision model.</summary>
public sealed class DecisionUseOptions
{
    /// <summary>False switches this use off; its caller keeps the deterministic answer. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Overrides the caller's default threshold.</summary>
    public double? Threshold { get; set; }
}

/// <summary>
/// <see cref="IDecisionService"/> backed by a systemone decision model on Microsoft Foundry
/// (<c>Microsoft-Decision-1</c>). Response shapes, observed 2026-10-13:
/// noul <c>{"noul":0.98,"type":"noul"}</c>; choice
/// <c>{"choice":"high","confidence":0.85,"probabilities":{"low":…},"type":"choice"}</c>; score
/// <c>{"score":1.91,"confidence":0.18,"legend":{"0":…},"probabilities":{"0":…},"type":"score"}</c>.
/// </summary>
public sealed class FoundryDecisionService : IDecisionService
{
    private const int MaxLoggedBody = 1000;

    private readonly IOptions<DecisionModelOptions> _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>Creates the service. <paramref name="httpClient"/> is for tests; by default it owns one.</summary>
    public FoundryDecisionService(
        IOptions<DecisionModelOptions> options,
        ILogger<FoundryDecisionService>? logger = null,
        HttpClient? httpClient = null)
    {
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeout below
        };
    }

    /// <inheritdoc />
    public bool IsEnabled(string purpose)
    {
        var options = _options.Value;
        return options.IsConfigured && (!options.Uses.TryGetValue(purpose, out var use) || use.Enabled);
    }

    /// <inheritdoc />
    public double ThresholdFor(string purpose, double fallback) =>
        _options.Value.Uses.TryGetValue(purpose, out var use) && use.Threshold is { } t ? t : fallback;

    /// <inheritdoc />
    public async Task<DecisionResult?> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled(request.Purpose)) return null;
        var options = _options.Value;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.5, options.TimeoutSeconds)));

        var url = options.Endpoint!.TrimEnd('/') + "/" + options.Path.TrimStart('/');
        // StringContent, not JsonContent: the systemone endpoint rejects a request without
        // Content-Length (HTTP 400 no_content_length_header), and JsonContent streams without one.
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(BuildBody(options.Deployment!, request).ToJsonString(),
                System.Text.Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("api-key", options.ApiKey);

        var sw = Stopwatch.StartNew();
        var outcome = "ok";
        try
        {
            using var response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                outcome = (int)response.StatusCode == 429 ? "rate-limited" : "http-error";
                _logger.LogWarning("Decision model ({Purpose}) returned HTTP {Status}: {Body}",
                    request.Purpose, (int)response.StatusCode, Truncate(body, MaxLoggedBody));
                return null;
            }

            var result = ParseResult(body);
            if (result is null || request.Questions.Keys.Any(k => !result.Answers.ContainsKey(k)))
            {
                outcome = "unreadable";
                _logger.LogWarning("Decision model ({Purpose}) response did not answer every question: {Body}",
                    request.Purpose, Truncate(body, MaxLoggedBody));
                return null;
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = "timeout";
            _logger.LogWarning("Decision model ({Purpose}) timed out after {Seconds}s", request.Purpose, options.TimeoutSeconds);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            outcome = "error";
            _logger.LogWarning(ex, "Decision model ({Purpose}) call failed", request.Purpose);
            return null;
        }
        finally
        {
            var tags = new TagList
            {
                { "rockbot.decision.purpose", request.Purpose },
                { "rockbot.decision.outcome", outcome },
            };
            HostDiagnostics.DecisionCalls.Add(1, tags);
            HostDiagnostics.DecisionDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
        }
    }

    /// <summary>The systemone request body.</summary>
    internal static JsonObject BuildBody(string deployment, DecisionRequest request)
    {
        var questions = new JsonObject();
        foreach (var (name, question) in request.Questions)
            questions[name] = BuildQuestion(question);

        return new JsonObject
        {
            ["model"] = deployment,
            ["state"] = request.State is string text ? JsonValue.Create(text) : JsonSerializer.SerializeToNode(request.State),
            ["questions"] = questions,
        };
    }

    private static JsonObject BuildQuestion(DecisionQuestion question)
    {
        switch (question)
        {
            case NoulQuestion noul:
            {
                var q = new JsonObject { ["type"] = "noul", ["instructions"] = noul.Instructions };
                if (noul.WhenTrue is not null || noul.WhenFalse is not null)
                {
                    var criteria = new JsonObject();
                    if (noul.WhenTrue is not null) criteria["true"] = noul.WhenTrue;
                    if (noul.WhenFalse is not null) criteria["false"] = noul.WhenFalse;
                    q["criteria"] = criteria;
                }
                return q;
            }
            case ChoiceQuestion choice:
            {
                var criteria = new JsonObject();
                foreach (var (label, description) in choice.Options) criteria[label] = description;
                return new JsonObject { ["type"] = "choice", ["instructions"] = choice.Instructions, ["criteria"] = criteria };
            }
            case ScoreQuestion score:
                return new JsonObject
                {
                    ["type"] = "score",
                    ["instructions"] = score.Instructions,
                    ["criteria"] = new JsonArray(score.Levels.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
                };
            default:
                throw new ArgumentException($"Unsupported question type {question.GetType().Name}", nameof(question));
        }
    }

    /// <summary>Reads a systemone response; null when it has no <c>answers</c> object.</summary>
    internal static DecisionResult? ParseResult(string body)
    {
        if (JsonNode.Parse(body) is not JsonObject root || root["answers"] is not JsonObject answers)
            return null;

        var parsed = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (name, node) in answers)
        {
            if (ParseAnswer(node) is { } answer)
                parsed[name] = answer;
        }
        return new DecisionResult(parsed, (string?)root["model"]);
    }

    private static DecisionAnswer? ParseAnswer(JsonNode? node)
    {
        if (node is null) return null;
        if (TryNumber(node, out var bare)) return new DecisionAnswer(Probability: Clamp(bare));
        if (node is not JsonObject obj) return null;

        double? probability = null;
        foreach (var name in (string[])["noul", "probability", "p_true", "p"])
        {
            if (obj[name] is { } n && TryNumber(n, out var v)) { probability = Clamp(v); break; }
        }

        string? choice = obj["choice"] is JsonValue c && c.TryGetValue(out string? s) ? s : null;
        double? score = obj["score"] is { } sn && TryNumber(sn, out var sv) ? sv : null;
        double? confidence = obj["confidence"] is { } cn && TryNumber(cn, out var cv) ? cv : null;

        Dictionary<string, double>? probabilities = null;
        if (obj["probabilities"] is JsonObject probs)
        {
            probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (label, value) in probs)
                if (value is not null && TryNumber(value, out var p)) probabilities[label] = p;
        }

        if (probability is null && choice is null && score is null) return null;
        return new DecisionAnswer(probability, choice, score, confidence, probabilities);
    }

    private static bool TryNumber(JsonNode node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.TryGetValue(out value);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
