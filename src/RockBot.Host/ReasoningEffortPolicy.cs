using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace RockBot.Host;

/// <summary>
/// Adds the non-standard <c>reasoning</c> object to outgoing chat-completion request bodies,
/// selecting how much thinking a reasoning-capable model does before it answers.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.Extensions.AI</c>'s <see cref="Microsoft.Extensions.AI.ChatOptions"/> models
/// only the OpenAI-standard sampling knobs, so there is no supported way to set reasoning
/// effort through it. OpenRouter accepts <c>{"reasoning":{"effort":"low|medium|high"}}</c> and
/// forwards it to providers whose models support it; providers that do not simply ignore it.
/// </para>
/// <para>
/// Injecting at the pipeline level rather than through <c>ChatOptions</c> keeps this
/// independent of the MEAI surface: the body is already serialised, so the policy parses it,
/// adds one field, and re-serialises. Requests that are not chat completions (no
/// <c>messages</c> array) and bodies that already carry the field are left untouched, as is
/// any body that fails to parse — a malformed rewrite would break the call outright, so the
/// policy always fails open.
/// </para>
/// <para>
/// Effort is a per-tier setting because the tiers do different work: a tier that writes prose
/// and a tier that runs batch extraction do not want the same budget. It is configured as
/// <c>LLM:&lt;Tier&gt;:ReasoningEffort</c> alongside that tier's endpoint and model.
/// </para>
/// </remarks>
public sealed class ReasoningEffortPolicy(string effort) : PipelinePolicy
{
    /// <summary>
    /// The values OpenRouter accepts. Anything else is rejected at construction rather than
    /// sent, because an unrecognised effort is silently dropped by the provider and looks
    /// exactly like a working setting from the outside.
    /// </summary>
    private static readonly string[] Valid = ["low", "medium", "high"];

    /// <summary>
    /// Returns the normalised effort when <paramref name="value"/> is one this policy can
    /// send, or <see langword="null"/> when it is empty or unrecognised. Callers use this to
    /// decide whether to register the policy at all.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().ToLowerInvariant();
        return Array.IndexOf(Valid, trimmed) >= 0 ? trimmed : null;
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
    /// Returns the request body with <c>reasoning</c> added, or <see langword="null"/> when the
    /// body should be sent through unchanged — it is not a chat completion, it already carries
    /// the field, or it is not a JSON object.
    /// </summary>
    internal static string? InjectInto(ReadOnlySpan<byte> body, string effort)
    {
        if (JsonNode.Parse(System.Text.Encoding.UTF8.GetString(body)) is not JsonObject json)
            return null;

        // Only chat completions; embeddings and other calls reject the field.
        if (!json.ContainsKey("messages")) return null;

        // An explicit value from the caller always wins.
        if (json.ContainsKey("reasoning")) return null;

        json["reasoning"] = new JsonObject { ["effort"] = effort };
        return json.ToJsonString();
    }
}
