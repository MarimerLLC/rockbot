using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace RockBot.Host;

/// <summary>
/// Adds the non-standard <c>repetition_penalty</c> field to outgoing chat-completion
/// request bodies.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.Extensions.AI</c>'s <see cref="Microsoft.Extensions.AI.ChatOptions"/> models
/// only the OpenAI-standard sampling knobs, so there is no supported way to set
/// <c>repetition_penalty</c> through it. Many OpenAI-compatible hosts (vLLM, TGI, OpenRouter
/// and most of its providers) do accept the field, and for long-form conversational models it
/// is the parameter that actually prevents a reply being replayed word for word — see
/// <see cref="AgentHostOptions.RepetitionPenalty"/> for why frequency penalty does not.
/// </para>
/// <para>
/// Injecting at the pipeline level rather than through <c>ChatOptions</c> keeps this
/// independent of the MEAI surface: the body is already serialised, so the policy parses it,
/// adds one field, and re-serialises. Requests that are not chat completions (no
/// <c>messages</c> array) and bodies that already carry the field are left untouched, as is
/// any body that fails to parse — a malformed rewrite would break the call outright, so the
/// policy always fails open.
/// </para>
/// </remarks>
public sealed class RepetitionPenaltyPolicy(float penalty) : PipelinePolicy
{
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

            var rewritten = InjectInto(buffer.ToArray(), penalty);
            if (rewritten is null) return;

            request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
        }
        catch
        {
            // Fail open: send the original body unmodified rather than break the request.
        }
    }

    /// <summary>
    /// Returns the request body with <c>repetition_penalty</c> added, or <see langword="null"/>
    /// when the body should be sent through unchanged — it is not a chat completion, it already
    /// carries the field, or it is not a JSON object.
    /// </summary>
    internal static string? InjectInto(ReadOnlySpan<byte> body, float penalty)
    {
        if (JsonNode.Parse(System.Text.Encoding.UTF8.GetString(body)) is not JsonObject json)
            return null;

        // Only chat completions; embeddings and other calls reject the field.
        if (!json.ContainsKey("messages")) return null;

        // An explicit value from the caller always wins.
        if (json.ContainsKey("repetition_penalty")) return null;

        json["repetition_penalty"] = penalty;
        return json.ToJsonString();
    }
}
