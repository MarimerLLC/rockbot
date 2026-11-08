using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace RockBot.Host;

/// <summary>
/// Adds Anthropic <c>cache_control</c> breakpoints to outgoing chat-completion request
/// bodies routed to a Claude model through OpenRouter's OpenAI-compatible endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Anthropic prompt caching is opt-in per request — unlike OpenAI's automatic server-side
/// caching, nothing is cached unless a content block explicitly carries
/// <c>"cache_control": {"type": "ephemeral"}</c>. <see cref="Microsoft.Extensions.AI.ChatOptions"/>
/// has no concept of this, so — following <see cref="ReasoningEffortPolicy"/> and
/// <see cref="RepetitionPenaltyPolicy"/> — it is injected into the already-serialised body
/// rather than expressed through the client surface.
/// </para>
/// <para>
/// Two breakpoints are placed, both allowed within OpenRouter's four-breakpoint cap
/// (openrouter.ai/docs/prompt-caching):
/// </para>
/// <list type="number">
/// <item>
/// <b>End of the system message.</b> Render order for an Anthropic-compatible request is
/// tools, then system, then messages, so a breakpoint at the end of the system content
/// caches the tool schemas and the system prompt together — the single largest block that
/// is byte-identical on every turn of a session (measured: 40K-90K tokens per RockBot
/// session, unchanged turn to turn).
/// </item>
/// <item>
/// <b>Second-to-last entry in <c>messages</c>.</b> In a long tool-calling loop the message
/// history itself becomes the dominant cost (measured: a 25-turn scheduled-task run grew
/// past 200K tokens of accumulated tool results). Marking everything up to "one turn ago"
/// cacheable means each new turn only pays full price for what it actually added, instead
/// of re-billing the entire growing transcript every time.
/// </item>
/// </list>
/// <para>
/// Both breakpoints are reapplied fresh on every call from the current message array, so the
/// incremental one moves forward automatically as the conversation grows — there is no state
/// to track between calls.
/// </para>
/// <para>
/// A message whose <c>content</c> is a plain string is converted to the one-element content-
/// array form Anthropic requires for a block-level breakpoint; a message that already uses
/// array content has the breakpoint added to its last element only if that element does not
/// already carry one. Requests that are not chat completions, have no system message, or have
/// fewer than two messages are left as untouched as possible (the system breakpoint still
/// applies on its own). A body that fails to parse, or where no breakpoint could be placed, is
/// sent through completely unmodified — a malformed rewrite would break the call outright, so
/// this always fails open.
/// </para>
/// <para>
/// Registration should be gated on both <see cref="OpenRouterAttributionPolicy.IsOpenRouterEndpoint(string?)"/>
/// and <see cref="IsClaudeModel"/> — cache_control is an Anthropic extension, not something to
/// send to an unrelated model or provider on the chance it is silently ignored.
/// </para>
/// </remarks>
public sealed class PromptCachingPolicy : PipelinePolicy
{
    /// <summary>True when <paramref name="modelId"/> names a Claude/Anthropic model.</summary>
    public static bool IsClaudeModel(string? modelId) =>
        modelId is not null && modelId.Contains("claude", StringComparison.OrdinalIgnoreCase);

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

            var rewritten = InjectInto(buffer.ToArray());
            if (rewritten is null) return;

            request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
        }
        catch
        {
            // Fail open: send the original body unmodified rather than break the request.
        }
    }

    /// <summary>
    /// Returns the request body with cache_control breakpoints added, or
    /// <see langword="null"/> when nothing changed — it is not a chat completion, or no
    /// breakpoint could be placed.
    /// </summary>
    internal static string? InjectInto(ReadOnlySpan<byte> body)
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
        if (json["messages"] is not JsonArray messages || messages.Count == 0) return null;

        var changed = false;

        // Breakpoint 1: end of the first system message — caches tools + system together.
        foreach (var entry in messages)
        {
            if (entry is not JsonObject msgObj) continue;
            if ((string?)msgObj["role"] != "system") continue;
            if (MarkLastContentBlockCacheable(msgObj)) changed = true;
            break;
        }

        // Breakpoint 2: second-to-last message — caches history up to "one turn ago", so
        // each new turn only pays full price for what it actually added.
        if (messages.Count >= 2 && messages[^2] is JsonObject prior)
        {
            if (MarkLastContentBlockCacheable(prior)) changed = true;
        }

        return changed ? json.ToJsonString() : null;
    }

    /// <summary>
    /// Converts a plain-string <c>content</c> to the one-element array form and tags it
    /// cacheable, or tags the last element of an existing array if not already tagged.
    /// Returns <see langword="false"/> (no change) for content shapes it does not recognise,
    /// or when the last element is already tagged.
    /// </summary>
    private static bool MarkLastContentBlockCacheable(JsonObject msgObj)
    {
        var content = msgObj["content"];

        if (content is JsonValue value && value.TryGetValue(out string? text))
        {
            msgObj["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                }
            };
            return true;
        }

        if (content is JsonArray parts && parts.Count > 0 &&
            parts[^1] is JsonObject lastPart && !lastPart.ContainsKey("cache_control"))
        {
            lastPart["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            return true;
        }

        return false;
    }
}
