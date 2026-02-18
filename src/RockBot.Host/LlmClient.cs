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
    /// Calls the underlying chat client, retrying once if the model emits a tool call
    /// with null arguments. Some local models occasionally omit the arguments JSON entirely
    /// (returning <c>null</c> instead of the required minimum <c>{}</c>), which causes
    /// Microsoft.Extensions.AI to throw <see cref="ArgumentNullException"/> with parameter
    /// name <c>encodedArguments</c> during response parsing. A single retry usually produces
    /// a well-formed call.
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
    }
}
