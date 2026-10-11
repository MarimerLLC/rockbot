using System.ClientModel;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RockBot.Host;

/// <summary>
/// Adds OpenAI's <c>prompt_cache_key</c> to outgoing chat-completion request bodies (#693), so
/// requests that share a prompt prefix are routed to the backend that holds it in cache.
/// </summary>
/// <remarks>
/// <para>
/// The per-request accounting from #670 showed back-to-back iterations of one tool loop —
/// identical ~32K-token prefixes, a few hundred tokens apart — alternating between 0% and 99%
/// cached. Prefix caching only works when the request lands where the prefix is; the key is the
/// provider's routing hint for that.
/// </para>
/// <para>
/// The key is a hash of the run's session (<see cref="ToolCallSessionContext"/>, which
/// <see cref="AgentLoopRunner.RunAsync"/> binds and which flows into the HTTP call) and the
/// request's model: every iteration, pass and turn of one conversation on one model shares a key,
/// each subagent, patrol and worker session gets its own, and the session id itself never leaves
/// the process. Requests outside a run carry no key.
/// </para>
/// <para>
/// Follows <see cref="ReasoningEffortPolicy"/>: the serialised body is parsed, one field added,
/// and re-serialised; anything that is not a chat completion, already carries the field, or fails
/// to parse goes through unchanged. An endpoint that rejects the field (a 400 naming it) gets the
/// call re-sent once without it, and this client stops sending it.
/// </para>
/// </remarks>
public sealed class PromptCacheKeyPolicy(Action<string>? warn = null) : PipelinePolicy
{
    public const string FieldName = "prompt_cache_key";

    private static readonly string[] SupportedHostSuffixes =
        [".openai.azure.com", ".cognitiveservices.azure.com", ".services.ai.azure.com"];

    private readonly Action<string> _warn = warn ?? Console.Error.WriteLine;
    private volatile bool _disabled;

    /// <summary>True once an endpoint has rejected the field; the policy then sends nothing.</summary>
    public bool Disabled => _disabled;

    /// <summary>
    /// True for endpoints known to accept <c>prompt_cache_key</c>: OpenAI itself and Azure OpenAI.
    /// Other OpenAI-compatible hosts (OpenRouter and the like) are left alone.
    /// </summary>
    public static bool AppliesTo(Uri? endpoint)
    {
        var host = endpoint?.Host;
        if (string.IsNullOrEmpty(host)) return false;
        return host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
            || SupportedHostSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The key for <paramref name="sessionId"/> on <paramref name="model"/>: "rb-" and 32 hex chars.</summary>
    public static string Key(string sessionId, string? model)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\n" + (model ?? string.Empty)));
        return "rb-" + Convert.ToHexStringLower(hash)[..32];
    }

    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var original = TryApply(message);
        ProcessNext(message, pipeline, currentIndex);
        if (original is not null && IsRejection(message))
        {
            message.Request.Content = original;
            ProcessNext(message, pipeline, currentIndex);
        }
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var original = TryApply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        if (original is not null && IsRejection(message))
        {
            message.Request.Content = original;
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }
    }

    /// <summary>Adds the key to the request; returns the original body when it did, otherwise null.</summary>
    private BinaryContent? TryApply(PipelineMessage message)
    {
        if (_disabled || ToolCallSessionContext.SessionId is not { Length: > 0 } sessionId)
            return null;

        var request = message.Request;
        if (request?.Content is null) return null;

        try
        {
            using var buffer = new MemoryStream();
            request.Content.WriteTo(buffer);
            var bytes = buffer.ToArray();

            var rewritten = InjectInto(bytes, sessionId);
            if (rewritten is null) return null;

            request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
            return BinaryContent.Create(BinaryData.FromBytes(bytes));
        }
        catch
        {
            // Fail open: send the original body unmodified rather than break the request.
            return null;
        }
    }

    /// <summary>
    /// True when the response is a 400 that names the field: the endpoint does not accept it.
    /// Disables the policy so later calls go out without it.
    /// </summary>
    private bool IsRejection(PipelineMessage message)
    {
        if (message.Response is not { Status: 400 } response) return false;
        string text;
        try
        {
            text = response.Content.ToString();
        }
        catch
        {
            return false;
        }
        if (!text.Contains(FieldName, StringComparison.Ordinal)) return false;

        if (!_disabled)
        {
            _disabled = true;
            _warn($"WARNING: endpoint rejected {FieldName}; re-sending without it and no longer sending it on this client. " +
                  $"Response: {(text.Length > 300 ? text[..300] : text)}");
        }
        return true;
    }

    /// <summary>
    /// Returns the request body with <c>prompt_cache_key</c> added, or <see langword="null"/>
    /// when it should go through unchanged — not a chat completion, already keyed, or not a JSON object.
    /// </summary>
    internal static string? InjectInto(ReadOnlySpan<byte> body, string sessionId)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (json is null || !json.ContainsKey("messages") || json.ContainsKey(FieldName))
            return null;

        var model = json["model"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
        json[FieldName] = Key(sessionId, model);
        return json.ToJsonString();
    }
}
