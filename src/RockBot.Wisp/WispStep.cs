using System.Text.Json;
using System.Text.Json.Serialization;

namespace RockBot.Wisp;

/// <summary>
/// A single step in a wisp pipeline.
/// </summary>
public sealed record WispStep
{
    /// <summary>
    /// Unique identifier for this step within the wisp.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// The gateway type for tool routing. Required for <c>direct</c> mode steps;
    /// omitted for <c>llm</c> mode steps (the LLM uses its in-scope tools).
    /// </summary>
    [JsonPropertyName("gateway")]
    [JsonConverter(typeof(JsonStringEnumConverter<GatewayType>))]
    public GatewayType? Gateway { get; init; }

    /// <summary>
    /// Execution mode: <c>direct</c> (harness calls tool, zero LLM tokens) or
    /// <c>llm</c> (wisp LLM interprets and executes with minimal context).
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter<StepMode>))]
    public required StepMode Mode { get; init; }

    /// <summary>
    /// MCP server name (gateway=mcp only).
    /// </summary>
    [JsonPropertyName("server")]
    public string? Server { get; init; }

    /// <summary>
    /// Tool name to invoke (gateway=mcp, web only).
    /// </summary>
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    /// <summary>
    /// Script language (gateway=script only). Defaults to "python".
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>
    /// Tool parameters as a JSON object. Interpretation depends on the gateway type.
    /// Accepts both <c>"params"</c> and <c>"input"</c> as the JSON property name;
    /// if both are present, <c>"params"</c> wins.
    /// </summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    /// <summary>
    /// Alias for <see cref="Params"/>. LLMs sometimes use <c>"input"</c> instead of
    /// <c>"params"</c>; this property captures the value so the gateway router can
    /// fall back to it when <c>Params</c> is null.
    /// </summary>
    [JsonPropertyName("input")]
    public JsonElement? Input { get; init; }

    /// <summary>
    /// Alias for <see cref="Params"/>. LLMs sometimes use <c>"arguments"</c> instead of
    /// <c>"params"</c>.
    /// </summary>
    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }

    /// <summary>
    /// Resolved parameters: returns the first non-null of <see cref="Params"/>,
    /// <see cref="Input"/>, <see cref="Arguments"/>. As a last resort, if
    /// <see cref="InputFrom"/> looks like a JSON object (starts with '{'), it is
    /// parsed and used — LLMs sometimes stuff tool arguments into that field.
    /// </summary>
    [JsonIgnore]
    public JsonElement? ResolvedParams => Params ?? Input ?? Arguments ?? TryParseInputFromAsParams();

    /// <summary>
    /// Prompt/instruction for <c>llm</c> mode steps.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    /// <summary>
    /// A2A agent name (gateway=a2a only).
    /// </summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    /// <summary>
    /// A2A skill ID (gateway=a2a only).
    /// </summary>
    [JsonPropertyName("skill")]
    public string? Skill { get; init; }

    /// <summary>
    /// A2A message content (gateway=a2a only).
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Timeout in minutes for A2A steps. Defaults to 5.
    /// </summary>
    [JsonPropertyName("timeout_minutes")]
    public int? TimeoutMinutes { get; init; }

    /// <summary>
    /// File path or step reference to read input from.
    /// For <c>llm</c> steps, the harness reads the file and chunks into working memory.
    /// For <c>direct</c> steps, used for template substitution in params.
    /// Supports <c>{{steps.id.result}}</c> and file path references.
    /// </summary>
    [JsonPropertyName("input_from")]
    public string? InputFrom { get; init; }

    /// <summary>
    /// File path on the shared volume to write step output to.
    /// For <c>direct</c> steps: harness writes tool response content to the file.
    /// For <c>llm</c> steps: harness collects working memory writes and writes to the file.
    /// </summary>
    [JsonPropertyName("output_to")]
    public string? OutputTo { get; init; }

    /// <summary>
    /// Failure handling for <c>direct</c> steps. Defines what happens when this step fails.
    /// </summary>
    [JsonPropertyName("on_failure")]
    public OnFailureAction? OnFailure { get; init; }

    /// <summary>
    /// Attempts to parse <see cref="InputFrom"/> as a JSON object. LLMs sometimes
    /// stuff tool arguments into <c>input_from</c> as a JSON string instead of using
    /// <c>params</c>. Returns null if <c>InputFrom</c> is null, empty, or not valid JSON.
    /// </summary>
    private JsonElement? TryParseInputFromAsParams()
    {
        if (string.IsNullOrEmpty(InputFrom) || !InputFrom.TrimStart().StartsWith('{'))
            return null;

        try
        {
            return JsonDocument.Parse(InputFrom).RootElement;
        }
        catch
        {
            return null;
        }
    }
}
