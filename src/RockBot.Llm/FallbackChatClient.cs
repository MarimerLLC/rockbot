using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Llm;

internal enum FallbackErrorCategory { Transient, QuotaExhausted, HardError, Unknown }

/// <summary>
/// IChatClient decorator that holds an ordered list of model clients and falls back to
/// the next when the current is permanently degraded (quota/auth errors), while retrying
/// the same client with backoff for transient errors (429, timeout).
/// </summary>
public sealed class FallbackChatClient : IChatClient
{
    private readonly IReadOnlyList<(string ModelId, IChatClient Client)> _entries;
    private readonly ILogger _logger;
    private readonly bool[] _degraded;
    private readonly DateTimeOffset[] _degradedAt;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _cooldownPeriod;
    private readonly int _maxRetries;
    private volatile int _activeIndex;

    public FallbackChatClient(
        IReadOnlyList<(string ModelId, IChatClient Client)> entries,
        ILogger logger,
        TimeSpan? retryDelay = null,
        int maxRetries = 1,
        TimeSpan? cooldownPeriod = null)
    {
        if (entries.Count == 0)
            throw new ArgumentException("At least one entry is required.", nameof(entries));
        _entries = entries;
        _logger = logger;
        _degraded = new bool[entries.Count];
        _degradedAt = new DateTimeOffset[entries.Count];
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _cooldownPeriod = cooldownPeriod ?? TimeSpan.FromMinutes(5);
        _maxRetries = maxRetries;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialize once to avoid re-enumeration across retries/fallbacks.
        var messages = chatMessages as IReadOnlyList<ChatMessage> ?? chatMessages.ToList();

        // Cooldown recovery: if a degraded model's cooldown has elapsed, restore it
        // so the next iteration can retry the primary before falling back.
        RecoverCooledDownModels();

        for (int i = _activeIndex; i < _entries.Count; i++)
        {
            if (_degraded[i]) continue;

            var (modelId, client) = _entries[i];

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                if (attempt > 0 && _retryDelay > TimeSpan.Zero)
                    await Task.Delay(_retryDelay, cancellationToken);

                try
                {
                    return await client.GetResponseAsync(messages, options, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // User cancellation — do not retry or switch
                }
                catch (Exception ex)
                {
                    var category = ClassifyException(ex);

                    if (category == FallbackErrorCategory.Unknown)
                        throw; // Propagate immediately — don't retry or switch

                    if (category == FallbackErrorCategory.Transient && attempt < _maxRetries)
                        continue; // One more retry on the same client

                    // Transient retries exhausted, or permanent degradation
                    if (category is FallbackErrorCategory.QuotaExhausted or FallbackErrorCategory.HardError)
                    {
                        _degraded[i] = true;
                        _degradedAt[i] = DateTimeOffset.UtcNow;
                        _logger.LogWarning(
                            "FallbackChatClient: model {ModelId} marked degraded ({Category}); will retry after {Cooldown}",
                            modelId, category, _cooldownPeriod);

                        // Advance _activeIndex past this permanently-degraded slot
                        if (i == _activeIndex)
                            _activeIndex = i + 1;
                    }

                    break; // Fall through to next model
                }
            }

            // Log the fallback destination (if one exists)
            int nextIdx = -1;
            for (int j = i + 1; j < _entries.Count; j++)
            {
                if (!_degraded[j]) { nextIdx = j; break; }
            }

            if (nextIdx >= 0)
            {
                _logger.LogWarning(
                    "FallbackChatClient: falling back from {FromModelId} to {ToModelId}",
                    modelId, _entries[nextIdx].ModelId);
            }
        }

        _logger.LogWarning(
            "FallbackChatClient: all {Count} models degraded; no fallback available",
            _entries.Count);

        throw new InvalidOperationException(
            $"FallbackChatClient: all {_entries.Count} models are degraded; no fallback available.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int idx = _activeIndex;
        if (idx >= _entries.Count || _degraded[idx])
            throw new InvalidOperationException("FallbackChatClient: no active client available for streaming.");

        await foreach (var update in _entries[idx].Client
            .GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        int idx = _activeIndex;
        return idx < _entries.Count ? _entries[idx].Client.GetService(serviceType, serviceKey) : null;
    }

    public void Dispose()
    {
        foreach (var (_, client) in _entries)
            client.Dispose();
    }

    private void RecoverCooledDownModels()
    {
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!_degraded[i]) continue;
            if (now - _degradedAt[i] < _cooldownPeriod) continue;

            _degraded[i] = false;
            _logger.LogInformation(
                "FallbackChatClient: model {ModelId} cooldown elapsed; restored to active",
                _entries[i].ModelId);
        }

        // Reset _activeIndex to the earliest non-degraded model so the primary
        // is retried when it recovers, rather than staying pinned to the fallback.
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!_degraded[i])
            {
                _activeIndex = i;
                return;
            }
        }
    }

    private static FallbackErrorCategory ClassifyException(Exception ex)
    {
        if (ex is OperationCanceledException)
            return FallbackErrorCategory.Transient; // Treat timeout as transient

        if (ex is HttpRequestException { StatusCode: { } status })
        {
            return status switch
            {
                HttpStatusCode.TooManyRequests => FallbackErrorCategory.Transient,
                HttpStatusCode.PaymentRequired  => FallbackErrorCategory.QuotaExhausted,
                HttpStatusCode.Unauthorized     => FallbackErrorCategory.HardError,
                HttpStatusCode.Forbidden        => FallbackErrorCategory.HardError,
                HttpStatusCode.NotFound         => FallbackErrorCategory.HardError,
                _                               => FallbackErrorCategory.Unknown
            };
        }

        var msg = ex.Message;
        if (ContainsAny(msg, "credit", "quota", "billing", "insufficient_quota", "exceeded"))
            return FallbackErrorCategory.QuotaExhausted;

        return FallbackErrorCategory.Unknown;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));
}
