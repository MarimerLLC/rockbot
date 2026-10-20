using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Gives each request the <see cref="ModelBehavior.AdditionalSystemPrompt"/> of the model that
/// actually serves it, rather than the one resolved at startup (#730).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentContextBuilder"/> injects the prompt of the startup <see cref="ModelBehavior"/>
/// singleton, which is resolved once from the Balanced client's first model. Every other model
/// — a later entry of the fallback chain, the Low and High tiers — was sent that model's prompt
/// instead of its own. With Claude heading the chain, GPT turns got no GPT prompt at all.
/// </para>
/// <para>
/// This client wraps one concrete model, below <c>FallbackChatClient</c>, so it runs only once
/// the serving model is known. It drops the <see cref="ContextCategory.ModelBehavior"/> message
/// the builder injected and puts this model's own prompt in its place; when the builder injected
/// none, the prompt goes after the system prompt, client capabilities and rules — the same slot
/// the builder uses. A model without a prompt gets the builder's message removed. When the
/// builder already injected exactly this model's prompt, the request passes through untouched.
/// </para>
/// <para>
/// Behavior is resolved lazily on first use: the wrapped clients are built before the host's
/// service provider exists. A resolver that returns <see langword="null"/> (provider not ready)
/// leaves the request untouched and is asked again next call.
/// </para>
/// </remarks>
public sealed class ModelBehaviorPromptChatClient(
    IChatClient inner,
    string modelId,
    Func<string, ModelBehavior?> resolveBehavior,
    ILogger? logger = null) : DelegatingChatClient(inner)
{
    private string? _prompt;
    private volatile bool _resolved;

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Apply(messages), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(
                           Apply(messages), options, cancellationToken))
            yield return update;
    }

    internal IEnumerable<ChatMessage> Apply(IEnumerable<ChatMessage> messages)
    {
        if (!TryResolvePrompt(out var prompt)) return messages;

        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        var injected = new List<int>();
        for (var i = 0; i < list.Count; i++)
        {
            if (ContextTags.Get(list[i]) == ContextCategory.ModelBehavior)
                injected.Add(i);
        }

        if (prompt is null)
        {
            if (injected.Count == 0) return list;

            logger?.LogInformation(
                "Model behavior: removed the startup model's prompt for {ModelId}, which has none",
                modelId);
            return list.Where((_, i) => !injected.Contains(i)).ToList();
        }

        if (injected.Count == 1 && list[injected[0]].Text == prompt)
            return list;

        var result = new List<ChatMessage>(list.Count + 1);
        var insertAt = injected.Count > 0 ? injected[0] : PrefixEnd(list);
        for (var i = 0; i < list.Count; i++)
        {
            if (i == insertAt) result.Add(new ChatMessage(ChatRole.System, prompt).Tag(ContextCategory.ModelBehavior));
            if (!injected.Contains(i)) result.Add(list[i]);
        }
        if (insertAt >= list.Count)
            result.Add(new ChatMessage(ChatRole.System, prompt).Tag(ContextCategory.ModelBehavior));

        logger?.LogInformation(
            "Model behavior: {Action} AdditionalSystemPrompt for serving model {ModelId} ({Chars} chars)",
            injected.Count > 0 ? "swapped in" : "injected", modelId, prompt.Length);
        return result;
    }

    /// <summary>
    /// The index just past the leading system prompt, client-capability and rules messages —
    /// where <see cref="AgentContextBuilder"/> places the model-behavior prompt.
    /// </summary>
    private static int PrefixEnd(IReadOnlyList<ChatMessage> list)
    {
        if (list.Count == 0 || list[0].Role != ChatRole.System) return 0;

        var end = 1;
        while (end < list.Count
               && list[end].Role == ChatRole.System
               && ContextTags.Get(list[end]) is ContextCategory.ClientCapabilities or ContextCategory.Rules)
            end++;
        return end;
    }

    private bool TryResolvePrompt(out string? prompt)
    {
        if (!_resolved)
        {
            var behavior = resolveBehavior(modelId);
            if (behavior is null)
            {
                prompt = null;
                return false;
            }

            _prompt = string.IsNullOrEmpty(behavior.AdditionalSystemPrompt) ? null : behavior.AdditionalSystemPrompt;
            _resolved = true;
        }

        prompt = _prompt;
        return true;
    }
}
