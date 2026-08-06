using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Removes <see cref="ChatOptions.Tools"/> from the request forwarded to the inner
/// <see cref="IChatClient"/>, leaving the caller's own options instance untouched.
///
/// Used only on the text-based tool-calling path
/// (<see cref="RockBot.Llm.ModelBehavior.UseTextBasedToolCalling"/>). There, tool
/// descriptions are injected into the prompt and calls are parsed out of free text,
/// but <c>AgentLoopRunner</c> still needs <c>ChatOptions.Tools</c> populated to resolve
/// and dispatch a parsed call. Without this wrapper those tools are also serialised
/// into the provider request as a <c>tools</c> array — harmless for endpoints that
/// merely ignore it, but fatal for ones that reject it outright. OpenRouter, for
/// example, returns <c>404 "No endpoints found that support tool use"</c> for any model
/// with no tool-capable provider, so every request fails before the model is reached.
/// </summary>
public sealed class ToolStrippingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, WithoutTools(options), cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(
                           messages, WithoutTools(options), cancellationToken))
            yield return update;
    }

    /// <summary>
    /// Returns a clone of <paramref name="options"/> with <see cref="ChatOptions.Tools"/>
    /// and <see cref="ChatOptions.ToolMode"/> cleared. The original is never mutated —
    /// the caller reuses it across loop iterations to dispatch parsed tool calls.
    /// </summary>
    private static ChatOptions? WithoutTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 }) return options;

        var clone = options.Clone();
        clone.Tools = null;
        clone.ToolMode = null;
        return clone;
    }
}
