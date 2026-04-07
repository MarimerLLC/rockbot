using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Llm.Copilot;

/// <summary>
/// <see cref="IChatClient"/> adapter for the GitHub Copilot SDK.
/// Each call creates a new session, serializes the full message history into the prompt,
/// collects the response, and disposes the session.
/// </summary>
public sealed class CopilotChatClient : IChatClient
{
    private readonly CopilotClient _copilotClient;
    private readonly CopilotChatClientOptions _options;
    private readonly ILogger<CopilotChatClient> _logger;
    private readonly ChatClientMetadata _metadata;

    public CopilotChatClient(
        CopilotClient copilotClient,
        CopilotChatClientOptions options,
        ILogger<CopilotChatClient> logger)
    {
        _copilotClient = copilotClient ?? throw new ArgumentNullException(nameof(copilotClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metadata = new ChatClientMetadata("github-copilot", defaultModelId: options.ModelId);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await SendWithSessionAsync(chatMessages, cancellationToken)
                    .ConfigureAwait(false);

                sw.Stop();
                CopilotDiagnostics.RequestDuration.Record(sw.Elapsed.TotalMilliseconds);
                CopilotDiagnostics.RequestsSent.Add(1);
                return result;
            }
            catch (Exception ex) when (IsRateLimitError(ex) && attempt < _options.MaxRetries)
            {
                CopilotDiagnostics.RequestsRateLimited.Add(1);
                var delay = ComputeBackoff(attempt);
                _logger.LogWarning(
                    "Copilot rate-limited (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms",
                    attempt + 1, _options.MaxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRateLimitError(ex))
            {
                // Exhausted retries — wrap as 429 for FallbackChatClient.ClassifyException.
                throw new HttpRequestException(
                    "Copilot rate limit exceeded after retries",
                    ex,
                    HttpStatusCode.TooManyRequests);
            }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (systemPrompt, userPrompt) = MessageFormatter.Format(chatMessages);

        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        CopilotSession? session = null;
        IDisposable? subscription = null;

        try
        {
            session = await CreateSessionAsync(systemPrompt, cancellationToken)
                .ConfigureAwait(false);

            subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        var text = delta.Data?.DeltaContent;
                        if (!string.IsNullOrEmpty(text))
                        {
                            channel.Writer.TryWrite(new ChatResponseUpdate(
                                ChatRole.Assistant, text));
                        }
                        break;
                    case SessionIdleEvent:
                        channel.Writer.TryComplete();
                        break;
                    case SessionErrorEvent error:
                        channel.Writer.TryComplete(
                            new InvalidOperationException(
                                error.Data?.Message ?? "Copilot session error"));
                        break;
                }
            });

            CopilotDiagnostics.RequestsSent.Add(1);
            await session.SendAsync(new MessageOptions { Prompt = userPrompt }, cancellationToken)
                .ConfigureAwait(false);

            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            subscription?.Dispose();
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata))
            return _metadata;

        return null;
    }

    public void Dispose()
    {
        // CopilotClient is a shared singleton — not disposed here.
    }

    private async Task<ChatResponse> SendWithSessionAsync(
        IEnumerable<ChatMessage> chatMessages,
        CancellationToken cancellationToken)
    {
        var (systemPrompt, userPrompt) = MessageFormatter.Format(chatMessages);

        await using var session = await CreateSessionAsync(systemPrompt, cancellationToken)
            .ConfigureAwait(false);

        var response = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = userPrompt },
            _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);

        var content = response?.Data?.Content ?? string.Empty;

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            ModelId = _options.ModelId
        };
    }

    private async Task<CopilotSession> CreateSessionAsync(
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        var config = new SessionConfig
        {
            Model = _options.ModelId,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            AvailableTools = [] // No tools — RockBot controls tool calling
        };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            config.SystemMessage = new GitHub.Copilot.SDK.SystemMessageConfig
            {
                Mode = GitHub.Copilot.SDK.SystemMessageMode.Replace,
                Content = systemPrompt
            };
        }

        var session = await _copilotClient
            .CreateSessionAsync(config, cancellationToken)
            .ConfigureAwait(false);

        CopilotDiagnostics.SessionsCreated.Add(1);
        _logger.LogDebug("Created Copilot session {SessionId} with model {Model}",
            session.SessionId, _options.ModelId);

        return session;
    }

    private static bool IsRateLimitError(Exception ex)
    {
        // Check for HTTP 429 in nested HttpRequestException.
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            return true;

        // Check message heuristic for SDK-wrapped rate limit errors.
        return ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("429", StringComparison.Ordinal);
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseMs = _options.RetryBaseDelay.TotalMilliseconds;
        var delay = baseMs * Math.Pow(2, attempt);
        // Add jitter: +-25% of the delay.
        var jitter = delay * 0.25 * (Random.Shared.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(delay + jitter);
    }
}
