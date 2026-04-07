using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

        // Use event-driven approach: listen for the assistant message, then check for
        // tool requests. We use SendAsync + manual event handling (not SendAndWaitAsync)
        // so we can intercept tool requests before the CLI tries to execute them.
        var tcs = new TaskCompletionSource<AssistantMessageEvent?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent ame:
                    // Capture the first complete assistant message with tool requests.
                    // If it has tool requests, complete immediately — don't wait for idle.
                    if (ame.Data?.ToolRequests is { Length: > 0 })
                        tcs.TrySetResult(ame);
                    else
                        tcs.TrySetResult(ame);
                    break;
                case SessionIdleEvent:
                    // If idle without an assistant message, complete with null.
                    tcs.TrySetResult(null);
                    break;
                case SessionErrorEvent error:
                    tcs.TrySetException(new InvalidOperationException(
                        error.Data?.Message ?? "Copilot session error"));
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = userPrompt }, cancellationToken)
            .ConfigureAwait(false);

        // Wait for the assistant message with timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);
        var registration = timeoutCts.Token.Register(() =>
            tcs.TrySetCanceled(timeoutCts.Token));

        AssistantMessageEvent? ameResult;
        try
        {
            ameResult = await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }

        // Build the ChatResponse.
        var contents = new List<AIContent>();
        var textContent = ameResult?.Data?.Content;
        if (!string.IsNullOrEmpty(textContent))
            contents.Add(new TextContent(textContent));

        // Convert ToolRequests to FunctionCallContent for RockBot's native tool-calling pipeline.
        if (ameResult?.Data?.ToolRequests is { Length: > 0 } toolRequests)
        {
            _logger.LogDebug("Copilot returned {Count} tool request(s)", toolRequests.Length);
            foreach (var tr in toolRequests)
            {
                Dictionary<string, object?>? args = null;
                if (tr.Arguments is not null)
                {
                    try
                    {
                        // Arguments may be a JsonElement or a pre-deserialized object.
                        var json = tr.Arguments is JsonElement je
                            ? je.GetRawText()
                            : JsonSerializer.Serialize(tr.Arguments);
                        args = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize tool arguments for {Tool}", tr.Name);
                    }
                }

                contents.Add(new FunctionCallContent(
                    tr.ToolCallId ?? Guid.NewGuid().ToString(),
                    tr.Name ?? "unknown",
                    args));
            }
        }

        if (contents.Count == 0)
            contents.Add(new TextContent(string.Empty));

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = _options.ModelId
        };
    }

    private async Task<CopilotSession> CreateSessionAsync(
        string systemPrompt,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // Create stub AIFunctions with the same schema but no-op handlers.
        // The model sees tool definitions and can request calls; the Copilot CLI
        // will invoke the stub (returning a sentinel), but we intercept the
        // ToolRequests in the event handler and return FunctionCallContent to RockBot.
        var stubTools = (options?.Tools?
            .OfType<AIFunction>()
            .Select(CreateStubFunction)
            .ToList()) ?? [];

        var config = new SessionConfig
        {
            Model = _options.ModelId,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = stubTools,
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
        _logger.LogDebug("Created Copilot session {SessionId} with model {Model}, {ToolCount} tools",
            session.SessionId, _options.ModelId, stubTools.Count);

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

    /// <summary>
    /// Creates a stub AIFunction with the same metadata (name, description, schema)
    /// as the original but with a no-op handler. Registered on the Copilot session so the
    /// model sees tool definitions. If the CLI attempts to invoke the stub, it returns
    /// immediately — RockBot intercepts the ToolRequests from the event and handles
    /// execution itself.
    /// </summary>
    private static AIFunction CreateStubFunction(AIFunction original)
    {
        return new StubAIFunction(original);
    }

    private sealed class StubAIFunction : AIFunction
    {
        private readonly AIFunction _original;

        public StubAIFunction(AIFunction original) => _original = original;

        public override string Name => _original.Name;
        public override string Description => _original.Description;
        public override JsonElement JsonSchema => _original.JsonSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            // The CLI may invoke this stub if the model requests a tool call.
            // Return immediately so the session doesn't hang. Our event handler
            // captures the ToolRequests and returns them as FunctionCallContent.
            return new ValueTask<object?>("[tool_intercepted_by_rockbot]");
        }
    }
}
