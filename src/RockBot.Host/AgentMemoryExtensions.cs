using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RockBot.Host;

/// <summary>
/// Extension methods for registering agent memory systems.
/// </summary>
public static class AgentMemoryExtensions
{
    /// <summary>
    /// Registers conversation memory, long-term memory, and working memory with default options.
    /// </summary>
    public static AgentHostBuilder WithMemory(this AgentHostBuilder builder)
    {
        builder.WithConversationMemory();
        builder.WithLongTermMemory();
        builder.WithWorkingMemory();
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

    /// <summary>
    /// Registers session-scoped working memory (TTL-based scratch space for tool call results).
    /// </summary>
    public static AgentHostBuilder WithWorkingMemory(
        this AgentHostBuilder builder,
        Action<WorkingMemoryOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<WorkingMemoryOptions>(_ => { });

        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IWorkingMemory, HybridCacheWorkingMemory>();

        return builder;
    }

    /// <summary>
    /// Registers the periodic memory consolidation service (dreaming).
    /// Requires <see cref="ILongTermMemory"/> and <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// to be registered — call after <see cref="WithLongTermMemory"/> and the chat client setup.
    /// </summary>
    public static AgentHostBuilder WithDreaming(
        this AgentHostBuilder builder,
        Action<DreamOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<DreamOptions>(_ => { });

        builder.Services.AddSingleton<DreamService>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DreamService>());

        return builder;
    }
}
