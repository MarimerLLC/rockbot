using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace RockBot.Host;

/// <summary>
/// Adds the GPT-5 family's <c>verbosity</c> field to outgoing chat-completion request bodies,
/// steering how long the model's answers run.
/// </summary>
/// <remarks>
/// <para>
/// GPT reads a terse house style literally: the persona's "one- or two-sentence answers are
/// often the right answer" comes back as one or two sentences, where Claude reads the same
/// line as a voice and still gives context (#730). <c>verbosity</c> is the provider's own
/// length dial, independent of the prompt. Measured on <c>gpt-5.6-terra</c> via Azure with
/// one factual question: <c>low</c> 211 chars, unset 475, <c>medium</c> 517, <c>high</c> 903 —
/// so unset is <c>medium</c>.
/// </para>
/// <para>
/// Injected into the serialised body for the same reason as <see cref="RepetitionPenaltyPolicy"/>:
/// <see cref="Microsoft.Extensions.AI.ChatOptions"/> has no way to express it. Requests that
/// are not chat completions (no <c>messages</c> array), bodies that already carry the field,
/// and bodies that fail to parse are left untouched, so the policy always fails open.
/// </para>
/// </remarks>
public sealed class VerbosityPolicy(string verbosity) : PipelinePolicy
{
    /// <summary>
    /// Levels the provider accepts. Values outside this set are rejected at configuration time
    /// rather than sent, because an unrecognised level is a 400 on every call.
    /// </summary>
    private static readonly string[] KnownLevels = ["low", "medium", "high"];

    /// <summary>
    /// Returns the normalised level for <paramref name="value"/>, or <see langword="null"/>
    /// when it names no level the provider understands. Case- and whitespace-insensitive.
    /// </summary>
    public static string? Normalise(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(trimmed) && KnownLevels.Contains(trimmed) ? trimmed : null;
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

            var rewritten = InjectInto(buffer.ToArray(), verbosity);
            if (rewritten is null) return;

            request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
        }
        catch
        {
            // Fail open: send the original body unmodified rather than break the request.
        }
    }

    /// <summary>
    /// Returns the request body with <c>verbosity</c> added, or <see langword="null"/> when the
    /// body should be sent through unchanged — it is not a chat completion, it already carries
    /// the field, or it is not a JSON object.
    /// </summary>
    internal static string? InjectInto(ReadOnlySpan<byte> body, string verbosity)
    {
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
        if (json.ContainsKey("verbosity")) return null;

        json["verbosity"] = verbosity;
        return json.ToJsonString();
    }
}
