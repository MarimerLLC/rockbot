using Microsoft.Extensions.DependencyInjection;

namespace RockBot.Host;

/// <summary>
/// Extension methods for registering agent memory systems.
/// </summary>
public static class AgentMemoryExtensions
{
    /// <summary>
    /// Registers both conversation memory and long-term memory with default options.
    /// </summary>
    public static AgentHostBuilder WithMemory(this AgentHostBuilder builder)
    {
        builder.WithConversationMemory();
        builder.WithLongTermMemory();
        return builder;
    }

    /// <summary>
    /// Registers in-memory conversation memory with optional configuration.
    /// </summary>
    public static AgentHostBuilder WithConversationMemory(
        this AgentHostBuilder builder,
        Action<ConversationMemoryOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<ConversationMemoryOptions>(_ => { });

        builder.Services.AddSingleton<InMemoryConversationMemory>();
        builder.Services.AddSingleton<IConversationMemory>(sp => sp.GetRequiredService<InMemoryConversationMemory>());

        return builder;
    }

    /// <summary>
    /// Registers file-based long-term memory with optional configuration.
    /// </summary>
    public static AgentHostBuilder WithLongTermMemory(
        this AgentHostBuilder builder,
        Action<MemoryOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<MemoryOptions>(_ => { });

        builder.Services.AddSingleton<ILongTermMemory, FileMemoryStore>();

        return builder;
    }
}
