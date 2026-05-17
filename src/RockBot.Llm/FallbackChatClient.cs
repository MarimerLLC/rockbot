using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Llm;

internal enum FallbackErrorCategory { Transient, QuotaExhausted, ContentFilter, HardError, Unknown }

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
    private readonly TimeSpan _perAttemptTimeout;
    private readonly int _maxRetries;
    private volatile int _activeIndex;

    /// <summary>
    /// Raised when the client falls back from one model to another. Arguments are
    /// (fromModelId, toModelId, reason). Subscribers can use this to publish progress
    /// messages to the user so they know why things are taking longer.
    /// </summary>
    public event Action<string, string, string>? OnFallback;

    public FallbackChatClient(
        IReadOnlyList<(string ModelId, IChatClient Client)> entries,
        ILogger logger,
        TimeSpan? retryDelay = null,
        int maxRetries = 1,
        TimeSpan? cooldownPeriod = null,
        TimeSpan? perAttemptTimeout = null)
    {
        if (entries.Count == 0)
            throw new ArgumentException("At least one entry is required.", nameof(entries));
        _entries = entries;
        _logger = logger;
        _degraded = new bool[entries.Count];
        _degradedAt = new DateTimeOffset[entries.Count];
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _cooldownPeriod = cooldownPeriod ?? TimeSpan.FromMinutes(5);
        _perAttemptTimeout = perAttemptTimeout ?? TimeSpan.Zero;
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

                // Per-attempt timeout: each model attempt gets its own timeout window
                // so a stalled model doesn't consume the budget for fallback models.
                // The per-attempt CTS is linked to the caller's token so user
                // cancellation still propagates immediately.
                CancellationTokenSource? attemptCts = null;
                CancellationToken attemptCt = cancellationToken;
                if (_perAttemptTimeout > TimeSpan.Zero)
                {
                    attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attemptCts.CancelAfter(_perAttemptTimeout);
                    attemptCt = attemptCts.Token;
                }

                try
                {
                    return await client.GetResponseAsync(messages, options, attemptCt);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // User cancellation — do not retry or switch
                }
                catch (OperationCanceledException) when (attemptCts is not null && attemptCts.IsCancellationRequested)
                {
                    // Per-attempt timeout fired (not user cancellation) — treat as transient
                    _logger.LogWarning(
                        "FallbackChatClient: model {ModelId} timed out after {Timeout} (attempt {Attempt}/{MaxRetries})",
                        modelId, _perAttemptTimeout, attempt + 1, _maxRetries + 1);

                    if (attempt < _maxRetries)
                        continue; // Retry same model

                    NotifyFallback(i, modelId, "timeout");
                    break; // Fall through to next model
                }
                catch (Exception ex)
                {
                    var category = ClassifyException(ex);

                    if (category == FallbackErrorCategory.Unknown)
                        throw; // Propagate immediately — don't retry or switch

                    if (category == FallbackErrorCategory.Transient && attempt < _maxRetries)
                        continue; // One more retry on the same client

                    // Content filter: fall back to next model for this request only.
                    // Don't degrade — the model is fine, Azure's filter blocked the content.
                    if (category == FallbackErrorCategory.ContentFilter)
                    {
                        _logger.LogWarning(
                            "FallbackChatClient: content filter triggered on {ModelId}; trying next model without degrading",
                            modelId);
                        NotifyFallback(i, modelId, "content filter");
                        break;
                    }

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

                    NotifyFallback(i, modelId, category.ToString());
                    break; // Fall through to next model
                }
                finally
                {
                    attemptCts?.Dispose();
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
        if (serviceType == typeof(FallbackChatClient)) return this;
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

    private void NotifyFallback(int currentIndex, string fromModelId, string reason)
    {
        for (int j = currentIndex + 1; j < _entries.Count; j++)
        {
            if (!_degraded[j])
            {
                OnFallback?.Invoke(fromModelId, _entries[j].ModelId, reason);
                return;
            }
        }
    }

    private static FallbackErrorCategory ClassifyException(Exception ex)
    {
        if (ex is OperationCanceledException)
            return FallbackErrorCategory.Transient; // Treat timeout as transient

        // Azure's content filter surfaces as HTTP 400 with "content_filter" in the message
        // body. Status-code-only classification would map 400 to Unknown (immediate rethrow),
        // so check the message text first across all exception types.
        if (ContainsAny(ex.Message, "content_filter"))
            return FallbackErrorCategory.ContentFilter;

        if (ex is HttpRequestException { StatusCode: { } status })
        {
            return ClassifyStatusCode((int)status);
        }

        // OpenAI SDK (and other System.ClientModel-based SDKs) surface HTTP failures
        // as ClientResultException rather than HttpRequestException. Without this branch
        // a 503 from the LLM provider falls through to message-string inspection,
        // gets classified as Unknown, and is re-thrown without retry or fallback.
        if (ex is ClientResultException cre)
        {
            return ClassifyStatusCode(cre.Status);
        }

        if (ContainsAny(ex.Message, "credit", "quota", "billing", "insufficient_quota", "exceeded"))
            return FallbackErrorCategory.QuotaExhausted;

        return FallbackErrorCategory.Unknown;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static FallbackErrorCategory ClassifyStatusCode(int status) => status switch
    {
        408 => FallbackErrorCategory.Transient,        // Request Timeout
        429 => FallbackErrorCategory.Transient,        // Too Many Requests
        500 => FallbackErrorCategory.Transient,        // Internal Server Error
        502 => FallbackErrorCategory.Transient,        // Bad Gateway
        503 => FallbackErrorCategory.Transient,        // Service Unavailable
        504 => FallbackErrorCategory.Transient,        // Gateway Timeout
        402 => FallbackErrorCategory.QuotaExhausted,   // Payment Required
        401 or 403 or 404 => FallbackErrorCategory.HardError,
        _   => FallbackErrorCategory.Unknown
    };
}
