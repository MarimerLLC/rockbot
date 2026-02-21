using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Default implementation of <see cref="ILlmClient"/>.
/// Serializes all LLM calls through a <see cref="SemaphoreSlim"/> so that
/// at most one request is in flight at any time. Background tasks (memory
/// enrichment, skill summary generation, dreaming) queue behind the active
/// user-facing request rather than racing with it and triggering rate limits.
/// </summary>
internal sealed class LlmClient(IChatClient chatClient, ILogger<LlmClient> logger) : ILlmClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc/>
    public bool IsIdle => _gate.CurrentCount > 0;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await InvokeWithNullArgRetryAsync(messages, options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Calls the underlying chat client, retrying once on known model-specific SDK
    /// deserialization quirks:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <b>Null tool-call arguments</b> — some models omit the arguments JSON entirely
    ///       (returning <c>null</c> instead of <c>{}</c>), causing
    ///       <see cref="ArgumentNullException"/> with <c>encodedArguments</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>Unknown finish_reason</b> — some models (e.g. DeepSeek, Qwen via OpenRouter)
    ///       return finish reason values the OpenAI SDK does not recognise, causing
    ///       <see cref="ArgumentOutOfRangeException"/> from
    ///       <c>ChatFinishReasonExtensions.ToChatFinishReason</c>. A single retry usually
    ///       produces a well-formed response.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    private async Task<ChatResponse> InvokeWithNullArgRetryAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await chatClient.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ArgumentNullException ex) when (ex.ParamName == "encodedArguments")
        {
            logger.LogWarning(
                "LLM returned a tool call with null arguments (encodedArguments); retrying once");
            return await chatClient.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason"))
        {
            logger.LogWarning(
                "LLM returned an unrecognised finish_reason; retrying once. Detail: {Message}", ex.Message);
            return await chatClient.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
