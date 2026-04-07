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
/// Each call creates a single session with real tool handlers, sends the prompt,
/// and lets the Copilot CLI orchestrate the full tool-calling loop. The session
/// invokes RockBot's actual <see cref="AIFunction"/> implementations, feeds results
/// back to the model, and loops until idle — all within one session round-trip.
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
                var result = await SendWithSessionAsync(chatMessages, options, cancellationToken)
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
            session = await CreateSessionAsync(systemPrompt, options, cancellationToken)
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
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var (systemPrompt, userPrompt) = MessageFormatter.Format(chatMessages);

        await using var session = await CreateSessionAsync(systemPrompt, options, cancellationToken)
            .ConfigureAwait(false);

        // SendAndWaitAsync lets the Copilot CLI orchestrate the full tool-calling loop:
        // model requests tool → CLI invokes the real AIFunction handler → result fed to
        // model → repeat until the model responds with text and the session goes idle.
        // All tool execution happens within this single session round-trip.
        var response = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = userPrompt },
            _options.RequestTimeout,
            cancellationToken).ConfigureAwait(false);

        var content = response?.Data?.Content ?? string.Empty;

        _logger.LogDebug("Copilot session complete — {Length} chars response", content.Length);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            ModelId = _options.ModelId
        };
    }

    private async Task<CopilotSession> CreateSessionAsync(
        string systemPrompt,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // Pass real AIFunction handlers to the session. The Copilot CLI invokes
        // RockBot's actual tool implementations (web_search, memory, MCP, etc.),
        // feeds results back to the model, and loops until idle — all within a
        // single session. No stubs, no interception, no multiple round-trips.
        var tools = (options?.Tools?
            .OfType<AIFunction>()
            .ToList()) ?? [];

        var config = new SessionConfig
        {
            Model = _options.ModelId,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = tools,
        };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = systemPrompt
            };
        }

        var session = await _copilotClient
            .CreateSessionAsync(config, cancellationToken)
            .ConfigureAwait(false);

        CopilotDiagnostics.SessionsCreated.Add(1);
        _logger.LogDebug("Created Copilot session {SessionId} with model {Model}, {ToolCount} tools",
            session.SessionId, _options.ModelId, tools.Count);

        return session;
    }

    private static bool IsRateLimitError(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            return true;

        return ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("429", StringComparison.Ordinal);
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseMs = _options.RetryBaseDelay.TotalMilliseconds;
        var delay = baseMs * Math.Pow(2, attempt);
        var jitter = delay * 0.25 * (Random.Shared.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(delay + jitter);
    }
}
