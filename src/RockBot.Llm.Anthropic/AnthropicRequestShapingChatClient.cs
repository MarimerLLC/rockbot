using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace RockBot.Llm.Anthropic;

/// <summary>
/// Sits on top of the Anthropic SDK's <see cref="IChatClient"/> and shapes each request the way
/// RockBot needs: a top-level <c>cache_control</c> marker so the stable prompt prefix is cached,
/// the tier's reasoning effort, a real output cap, and no sampling parameters. SDK errors are
/// rethrown as <see cref="ClientResultException"/> so fallback classification and
/// context-overflow recovery, written against the OpenAI SDK, handle them unchanged.
/// </summary>
internal sealed class AnthropicRequestShapingChatClient : DelegatingChatClient
{
    private readonly string _modelId;
    private readonly int _defaultMaxOutputTokens;
    private readonly CacheControlEphemeral? _cacheControl;
    private readonly ApiEnum<string, Effort>? _effort;

    public AnthropicRequestShapingChatClient(
        IChatClient inner,
        string modelId,
        int defaultMaxOutputTokens,
        CacheControlEphemeral? cacheControl,
        ApiEnum<string, Effort>? effort)
        : base(inner)
    {
        _modelId = modelId;
        _defaultMaxOutputTokens = defaultMaxOutputTokens;
        _cacheControl = cacheControl;
        _effort = effort;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(PlaceSystemMessages(messages), Shape(options), cancellationToken);
        }
        catch (AnthropicException ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A yield cannot sit inside a try with a catch, so the enumerator is driven by hand.
        await using var updates = base.GetStreamingResponseAsync(PlaceSystemMessages(messages), Shape(options), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await updates.MoveNextAsync()) yield break;
            }
            catch (AnthropicException ex) when (Translate(ex) is { } translated)
            {
                throw translated;
            }
            yield return updates.Current;
        }
    }

    /// <summary>
    /// Leading system messages become the top-level <c>system</c> prompt; the SDK sends any later
    /// one as a mid-conversation <c>{"role":"system"}</c> message, which the API accepts only after
    /// a user turn (tool results count). RockBot's loop also injects system nudges straight after
    /// an assistant reply (completion review, hallucination checks), so those are re-sent as user
    /// messages. Rewriting in place, rather than hoisting into the system prompt, keeps the
    /// earlier prefix unchanged so the prompt cache still hits.
    /// </summary>
    internal static IEnumerable<ChatMessage> PlaceSystemMessages(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        List<ChatMessage>? rewritten = null;
        ChatRole? previous = null;   // last non-system role; null while still in the leading block

        for (var i = 0; i < list.Count; i++)
        {
            var message = list[i];
            if (message.Role != ChatRole.System)
                previous = message.Role;
            else if (previous == ChatRole.Assistant)
            {
                rewritten ??= [.. list];
                rewritten[i] = new ChatMessage(ChatRole.User, message.Contents)
                {
                    AuthorName = message.AuthorName,
                    AdditionalProperties = message.AdditionalProperties,
                };
            }
        }

        return rewritten ?? list;
    }

    internal ChatOptions Shape(ChatOptions? options)
    {
        var shaped = options?.Clone() ?? new ChatOptions();

        // Current Claude models reject any non-default temperature/top_p/top_k with a 400, and
        // RockBot's defaults were tuned for other providers. Dropped rather than forwarded.
        shaped.Temperature = null;
        shaped.TopP = null;
        shaped.TopK = null;

        // The SDK only fills model and max_tokens itself when no factory is supplied, so the
        // factory has to carry both. Captured now: the factory runs after the options are read.
        var callerFactory = shaped.RawRepresentationFactory;
        var modelId = shaped.ModelId ?? _modelId;
        var maxTokens = shaped.MaxOutputTokens ?? _defaultMaxOutputTokens;

        shaped.RawRepresentationFactory = client =>
        {
            var p = callerFactory?.Invoke(client) as MessageCreateParams
                ?? new MessageCreateParams { Model = modelId, MaxTokens = maxTokens, Messages = [] };

            // Automatic caching: the breakpoint sits on the last cacheable block and moves
            // forward as the conversation grows, so each tool-loop iteration reads everything
            // the previous one wrote.
            if (_cacheControl is not null && p.CacheControl is null)
                p = p with { CacheControl = _cacheControl };

            if (_effort is { } effort && p.OutputConfig?.Effort is null)
                p = p with { OutputConfig = (p.OutputConfig ?? new OutputConfig()) with { Effort = effort } };

            return p;
        };

        return shaped;
    }

    /// <summary>
    /// Maps SDK failures onto <see cref="ClientResultException"/>: an HTTP error keeps its status
    /// and response body (which carries Anthropic's error text, e.g. "credit balance is too low"
    /// or "prompt is too long"); a transport failure gets status 0, which callers already read
    /// as "no response received". Anything else is left alone.
    /// </summary>
    internal static ClientResultException? Translate(AnthropicException ex) => ex switch
    {
        AnthropicApiException api => new ClientResultException(
            api.Message, new TranslatedPipelineResponse((int)api.StatusCode, api.ResponseBody), api),
        AnthropicIOException io => new ClientResultException(io.Message, response: null, io),
        _ => null,
    };

    private sealed class TranslatedPipelineResponse(int status, string body) : PipelineResponse
    {
        private readonly BinaryData _content = BinaryData.FromString(body);

        public override int Status => status;
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => _content;
        protected override PipelineResponseHeaders HeadersCore { get; } = new NoHeaders();
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => _content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_content);
        public override void Dispose() { }

        private sealed class NoHeaders : PipelineResponseHeaders
        {
            public override bool TryGetValue(string name, out string? value) { value = null; return false; }
            public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
                Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        }
    }
}
