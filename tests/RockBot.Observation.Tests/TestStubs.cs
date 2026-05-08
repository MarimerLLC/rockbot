using Microsoft.Extensions.AI;
using RockBot.Host;

namespace RockBot.Observation.Tests;

/// <summary>
/// Minimal <see cref="ILlmClient"/> stub returning canned responses keyed by
/// the conversationId mentioned in the user prompt. Throws on cancellation
/// to match production behavior. Tests can also configure it to throw on
/// specific conversations to exercise per-conversation failure paths.
/// </summary>
internal sealed class StubLlmClient : ILlmClient
{
    private readonly Dictionary<string, string> _responsesByConversationId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _throwOnConversationIds = new(StringComparer.Ordinal);

    public int CallCount { get; private set; }

    public StubLlmClient AddResponse(string conversationId, string responseJson)
    {
        _responsesByConversationId[conversationId] = responseJson;
        return this;
    }

    public StubLlmClient ThrowFor(string conversationId)
    {
        _throwOnConversationIds.Add(conversationId);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
        => GetResponseAsync(messages, ModelTier.Balanced, options, cancellationToken);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;

        var userText = string.Concat(messages
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Text));

        var conversationId = ExtractConversationId(userText);
        if (conversationId is not null && _throwOnConversationIds.Contains(conversationId))
            throw new InvalidOperationException($"simulated extractor failure for {conversationId}");

        var response = conversationId is not null && _responsesByConversationId.TryGetValue(conversationId, out var r)
            ? r
            : "{\"observations\": []}";

        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
        return Task.FromResult(chatResponse);
    }

    private static string? ExtractConversationId(string text)
    {
        const string prefix = "Conversation ID: ";
        var idx = text.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + prefix.Length;
        var end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        return text[start..end].Trim();
    }
}

/// <summary>
/// Embedding generator that returns deterministic vectors derived from input
/// text. Two inputs mapped to the same "category key" produce highly similar
/// vectors; different categories produce orthogonal vectors. Categories and
/// fallback texts are assigned distinct dimensions in registration order so
/// tests cannot collide on hash bucketing.
/// </summary>
internal sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 64;

    private readonly Dictionary<string, float[]> _vectorsByText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _categoryByText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _categoryDimension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _textDimension = new(StringComparer.Ordinal);
    private int _nextDimension;

    /// <summary>Map a text input to a specific vector for the test.</summary>
    public StubEmbeddingGenerator With(string text, params float[] vector)
    {
        _vectorsByText[text] = vector;
        return this;
    }

    /// <summary>
    /// Group multiple texts into a category. All texts in the same category
    /// produce nearly-identical (cosine ~1.0) vectors; texts in different
    /// categories produce orthogonal vectors.
    /// </summary>
    public StubEmbeddingGenerator Category(string category, params string[] texts)
    {
        if (!_categoryDimension.ContainsKey(category))
            _categoryDimension[category] = NextDimension();
        foreach (var t in texts) _categoryByText[t] = category;
        return this;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var v in list)
            result.Add(new Embedding<float>(VectorFor(v)));
        return Task.FromResult(result);
    }

    private int NextDimension()
    {
        if (_nextDimension >= Dimensions)
            throw new InvalidOperationException(
                $"StubEmbeddingGenerator only supports {Dimensions} distinct categories/texts");
        return _nextDimension++;
    }

    private float[] VectorFor(string text)
    {
        if (_vectorsByText.TryGetValue(text, out var explicitVec))
            return explicitVec;

        if (_categoryByText.TryGetValue(text, out var cat))
        {
            var v = new float[Dimensions];
            v[_categoryDimension[cat]] = 1f;
            return v;
        }

        // Fallback: each unmapped text gets its own orthogonal dimension on
        // first sight.
        if (!_textDimension.TryGetValue(text, out var dim))
        {
            dim = NextDimension();
            _textDimension[text] = dim;
        }
        var fallback = new float[Dimensions];
        fallback[dim] = 1f;
        return fallback;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
