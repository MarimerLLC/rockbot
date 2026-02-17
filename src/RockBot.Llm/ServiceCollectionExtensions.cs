using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Llm;

/// <summary>
/// DI registration extensions for the LLM handler.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LLM request handler and subscribes to the "llm.request" topic.
    /// Requires an <see cref="Microsoft.Extensions.AI.IChatClient"/> to be registered separately.
    /// </summary>
    public static AgentHostBuilder AddLlmHandler(
        this AgentHostBuilder builder,
        Action<LlmOptions>? configure = null)
    {
        var options = new LlmOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.HandleMessage<LlmRequest, LlmRequestHandler>();
        builder.SubscribeTo("llm.request");

        return builder;
    }
}
