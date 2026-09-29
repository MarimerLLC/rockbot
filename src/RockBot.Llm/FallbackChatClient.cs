using System.ClientModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Llm;

internal enum FallbackErrorCategory { Transient, RateLimited, QuotaExhausted, ContentFilter, HardError, Unknown }

/// <summary>
/// IChatClient decorator that holds an ordered list of model clients and falls back to
/// the next when the current is permanently degraded (quota/auth errors), while retrying
/// the same client with backoff for transient errors (5xx, timeout).
/// </summary>
/// <remarks>
/// A 429 is handled differently from other transient errors: the model is paused for the
/// provider's <c>retry-after</c> window and the request moves straight to the next model.
/// Retrying a throttled model just queues behind the same tokens-per-minute ceiling — with
/// the SDK's own retry policy that meant ~30s per 429 until the per-attempt timeout fired,
/// then the same again on the retry, so a throttled turn cost ~3 minutes before it ever
/// reached the fallback. The pause is shared across callers so concurrent sessions stop
/// piling onto the throttled deployment. Entries that should fail over on 429 must be
/// built with SDK-level retries disabled, or the 429 never reaches this client.
/// </remarks>
public sealed class FallbackChatClient : IChatClient
{
    private readonly IReadOnlyList<(string ModelId, IChatClient Client)> _entries;
    private readonly ILogger _logger;
    private readonly bool[] _degraded;
    private readonly DateTimeOffset[] _degradedAt;
    private readonly long[] _rateLimitedUntilTicks;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _cooldownPeriod;
    private readonly TimeSpan _perAttemptTimeout;
    private readonly int _maxRetries;
    private readonly TimeSpan _defaultRateLimitPause;
    private readonly TimeSpan _maxRateLimitPause;
    private readonly TimeProvider _timeProvider;
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
        TimeSpan? perAttemptTimeout = null,
        TimeSpan? defaultRateLimitPause = null,
        TimeSpan? maxRateLimitPause = null,
        TimeProvider? timeProvider = null)
    {
        if (entries.Count == 0)
            throw new ArgumentException("At least one entry is required.", nameof(entries));
        _entries = entries;
        _logger = logger;
        _degraded = new bool[entries.Count];
        _degradedAt = new DateTimeOffset[entries.Count];
        _rateLimitedUntilTicks = new long[entries.Count];
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _cooldownPeriod = cooldownPeriod ?? TimeSpan.FromMinutes(5);
        _perAttemptTimeout = perAttemptTimeout ?? TimeSpan.Zero;
        _maxRetries = maxRetries;
        _defaultRateLimitPause = defaultRateLimitPause ?? TimeSpan.FromSeconds(30);
        _maxRateLimitPause = maxRateLimitPause ?? TimeSpan.FromMinutes(2);
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            // A throttled model is skipped while its retry-after window is open, but only
            // when something else can take the request — otherwise try it anyway rather
            // than fail a call the provider may well accept.
            if (IsRateLimited(i) && HasAvailableAfter(i)) continue;

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

                    if (category == FallbackErrorCategory.RateLimited)
                    {
                        var pause = GetRetryAfter(ex) ?? _defaultRateLimitPause;
                        if (pause > _maxRateLimitPause) pause = _maxRateLimitPause;
                        Volatile.Write(ref _rateLimitedUntilTicks[i],
                            (_timeProvider.GetUtcNow() + pause).UtcTicks);

                        if (HasAvailableAfter(i))
                        {
                            _logger.LogWarning(
                                "FallbackChatClient: model {ModelId} rate-limited (429); pausing it for {Pause} and trying the next model",
                                modelId, pause);
                            NotifyFallback(i, modelId, "rate limited");
                            break; // Fall through to next model
                        }

                        // Nothing to fall back to — waiting out the window is the only option.
                        if (attempt < _maxRetries)
                        {
                            _logger.LogWarning(
                                "FallbackChatClient: model {ModelId} rate-limited (429) with no fallback available; waiting {Pause}",
                                modelId, pause);
                            await Task.Delay(pause, _timeProvider, cancellationToken);
                            continue;
                        }

                        break;
                    }

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

    private bool IsRateLimited(int index) =>
        Volatile.Read(ref _rateLimitedUntilTicks[index]) > _timeProvider.GetUtcNow().UtcTicks;

    /// <summary>True when a later entry is neither degraded nor inside a rate-limit pause.</summary>
    private bool HasAvailableAfter(int index)
    {
        for (int j = index + 1; j < _entries.Count; j++)
        {
            if (!_degraded[j] && !IsRateLimited(j)) return true;
        }
        return false;
    }

    /// <summary>
    /// Reads the provider's retry hint from a 429. Azure OpenAI sends both
    /// <c>retry-after-ms</c> and <c>retry-after</c> (seconds); OpenAI sends the latter.
    /// Returns null when the exception carries no response or no usable header.
    /// </summary>
    internal static TimeSpan? GetRetryAfter(Exception ex)
    {
        if (ex is not ClientResultException cre) return null;

        try
        {
            var headers = cre.GetRawResponse()?.Headers;
            if (headers is null) return null;

            if (headers.TryGetValue("retry-after-ms", out var ms)
                && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var msValue)
                && msValue > 0)
                return TimeSpan.FromMilliseconds(msValue);

            if (headers.TryGetValue("retry-after", out var ra)
                && double.TryParse(ra, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
                return TimeSpan.FromSeconds(seconds);
        }
        catch (Exception)
        {
            // A response without readable headers is not worth failing the request over.
        }

        return null;
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

        if (ex is HttpRequestException hre)
        {
            // StatusCode is null when the request failed before any response was received
            // (DNS failure, TCP reset, socket close). Treat as transient so the next
            // model gets a chance.
            return hre.StatusCode is { } status
                ? ClassifyStatusCode((int)status)
                : FallbackErrorCategory.Transient;
        }

        // OpenAI SDK (and other System.ClientModel-based SDKs) surface HTTP failures
        // as ClientResultException rather than HttpRequestException. Without this branch
        // a 503 from the LLM provider falls through to message-string inspection,
        // gets classified as Unknown, and is re-thrown without retry or fallback.
        if (ex is ClientResultException cre)
        {
            // Status 0 means no HTTP response was received — the SDK's own retry
            // policy exhausted its budget on a transport-level failure ("Retry
            // failed after N tries."). Without this branch we'd classify it as
            // Unknown and never fall back to OpenRouter.
            return cre.Status == 0
                ? FallbackErrorCategory.Transient
                : ClassifyStatusCode(cre.Status);
        }

        // Socket / IO failures (DNS, TCP reset, "Resource temporarily unavailable")
        // wrapped inside another exception type. Walk the inner chain.
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException or IOException)
                return FallbackErrorCategory.Transient;
        }

        // SDK retry-exhaustion wrappers surface as a plain Exception whose message
        // is "Retry failed after N tries." with the underlying network error in
        // parentheses. Match the message so we still fall through to the next model.
        if (ContainsAny(ex.Message, "Retry failed after", "temporarily unavailable"))
            return FallbackErrorCategory.Transient;

        if (ContainsAny(ex.Message, "credit", "quota", "billing", "insufficient_quota", "exceeded"))
            return FallbackErrorCategory.QuotaExhausted;

        return FallbackErrorCategory.Unknown;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static FallbackErrorCategory ClassifyStatusCode(int status) => status switch
    {
        408 => FallbackErrorCategory.Transient,        // Request Timeout
        429 => FallbackErrorCategory.RateLimited,      // Too Many Requests
        500 => FallbackErrorCategory.Transient,        // Internal Server Error
        502 => FallbackErrorCategory.Transient,        // Bad Gateway
        503 => FallbackErrorCategory.Transient,        // Service Unavailable
        504 => FallbackErrorCategory.Transient,        // Gateway Timeout
        402 => FallbackErrorCategory.QuotaExhausted,   // Payment Required
        401 or 403 or 404 => FallbackErrorCategory.HardError,
        _   => FallbackErrorCategory.Unknown
    };
}
