using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Default implementation of <see cref="ILlmClient"/>.
/// Selects the appropriate <see cref="IChatClient"/> from the
/// <see cref="TieredChatClientRegistry"/> and adds retry logic for
/// known model-specific SDK quirks. Registered as transient so each consumer
/// gets its own instance — concurrent calls from the user loop, background tasks,
/// dreaming, and session evaluation proceed independently without queuing.
/// </summary>
internal sealed class LlmClient(TieredChatClientRegistry registry, ILogger<LlmClient> logger) : ILlmClient
{
    /// <summary>Calls the LLM using the Balanced tier.</summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetResponseAsync(messages, ModelTier.Balanced, options, cancellationToken);

    /// <summary>Calls the LLM using the specified tier.</summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = registry.GetClient(tier);
        var modelId = registry.GetModelId(tier) ?? tier.ToString();
        logger.LogInformation("LLM call: tier={Tier} model={ModelId}", tier, modelId);
        return InvokeWithNullArgRetryAsync(client, messages, options, cancellationToken);
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
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ArgumentNullException ex) when (ex.ParamName == "encodedArguments")
        {
            logger.LogWarning(
                "LLM returned a tool call with null arguments (encodedArguments); retrying once");
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason"))
        {
            logger.LogWarning(
                "LLM returned an unrecognised finish_reason; retrying once. Detail: {Message}", ex.Message);
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
