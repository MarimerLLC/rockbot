using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace RockBot.Host;

/// <summary>
/// Adds OpenRouter's nested <c>reasoning</c> object to outgoing chat-completion request
/// bodies, capping how many reasoning tokens a reasoning model spends before it answers.
/// </summary>
/// <remarks>
/// <para>
/// Reasoning models bill reasoning tokens as output and count them against
/// <c>max_tokens</c>, so an uncapped model can spend most of its output budget — and most of
/// the wall-clock — thinking. Measured on <c>x-ai/grok-4.6</c> via OpenRouter, an uncapped
/// request averaged ~2,900 reasoning tokens for a single paragraph of prose and ranged from
/// 667 to 6,407; the same request at <c>low</c> averaged ~670 with a far tighter spread.
/// </para>
/// <para>
/// <b>The OpenAI-standard <c>reasoning_effort</c> field does not work here.</b> Sending
/// <c>reasoning_effort: "low"</c> to OpenRouter measured no reduction at all (3,899 reasoning
/// tokens against a 2,574-token baseline on the same prompt) — it is accepted and ignored.
/// Only the nested <c>reasoning: { effort: … }</c> object actually constrains the model, and
/// <see cref="Microsoft.Extensions.AI.ChatOptions"/> has no way to express it. That is why
/// this is injected into the serialised body rather than set through ChatOptions or through
/// the OpenAI SDK's own reasoning-effort property.
/// </para>
/// <para>
/// <b><c>none</c> is not universally available.</b> Some endpoints refuse to run without
/// reasoning and answer <c>400 Reasoning is mandatory for this endpoint and cannot be
/// disabled.</c> — measured on <c>x-ai/grok-4.6</c> via OpenRouter. On such a model the
/// setting is not a cheaper tier, it fails every call, so verify it against the target
/// endpoint before configuring it. <c>minimal</c> is the safe floor there.
/// </para>
/// <para>
/// Follows <see cref="RepetitionPenaltyPolicy"/>: the body is already serialised, so the
/// policy parses it, adds one field, and re-serialises. Requests that are not chat
/// completions (no <c>messages</c> array), bodies that already carry a <c>reasoning</c>
/// field, and bodies that fail to parse are all left untouched — a malformed rewrite would
/// break the call outright, so the policy always fails open.
/// </para>
/// </remarks>
public sealed class ReasoningEffortPolicy(string effort) : PipelinePolicy
{
    /// <summary>
    /// Effort levels OpenRouter accepts inside the <c>reasoning</c> object. Values outside
    /// this set are rejected at construction rather than sent, because an unrecognised effort
    /// is a 400 from the provider on every single call.
    /// </summary>
    private static readonly string[] KnownEfforts = ["minimal", "low", "medium", "high"];

    /// <summary>Values that mean "turn reasoning off entirely" rather than naming a level.</summary>
    private static readonly string[] DisabledAliases = ["none", "off", "disabled", "false"];

    /// <summary>
    /// Returns the normalised effort for <paramref name="value"/>, or <see langword="null"/>
    /// when it names no level this provider understands. Case- and whitespace-insensitive.
    /// </summary>
    public static string? Normalise(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (DisabledAliases.Contains(trimmed)) return "none";
        return KnownEfforts.Contains(trimmed) ? trimmed : null;
    }

    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        TryApply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        TryApply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void TryApply(PipelineMessage message)
    {
        var request = message.Request;
        if (request?.Content is null) return;

        try
        {
            using var buffer = new MemoryStream();
            request.Content.WriteTo(buffer);

            var rewritten = InjectInto(buffer.ToArray(), effort);
            if (rewritten is null) return;

            request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
        }
        catch
        {
            // Fail open: send the original body unmodified rather than break the request.
        }
    }

    /// <summary>
    /// Returns the request body with the <c>reasoning</c> object added, or
    /// <see langword="null"/> when the body should be sent through unchanged — it is not a
    /// chat completion, it already carries the field, or it is not a JSON object.
    /// </summary>
    internal static string? InjectInto(ReadOnlySpan<byte> body, string effort)
    {
        // Parsed defensively rather than leaning on the caller's catch: a helper that throws
        // on malformed input is one refactor away from being called somewhere that does not
        // fail open.
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(body)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (json is null) return null;

        // Only chat completions; embeddings and other calls reject the field.
        if (!json.ContainsKey("messages")) return null;

        // An explicit value from the caller always wins.
        if (json.ContainsKey("reasoning")) return null;

        // "none" disables reasoning outright; every other value names a level. Sent as the
        // nested object in both cases — the flat reasoning_effort field is ignored here.
        json["reasoning"] = effort == "none"
            ? new JsonObject { ["enabled"] = false }
            : new JsonObject { ["effort"] = effort };

        return json.ToJsonString();
    }
}
