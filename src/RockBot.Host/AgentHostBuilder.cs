using Microsoft.Extensions.DependencyInjection;

namespace RockBot.Host;

/// <summary>
/// Fluent builder for configuring an agent host.
/// </summary>
public sealed class AgentHostBuilder
{
    private readonly IServiceCollection _services;
    private readonly MessageTypeResolver _resolver = new();
    private readonly AgentHostOptions _options = new();
    private AgentIdentity _identity = new("default-agent");

    internal AgentHostBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// The service collection for external extension methods to register services.
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Set the agent identity.
    /// </summary>
    public AgentHostBuilder WithIdentity(string name, string? instanceId = null)
    {
        _identity = instanceId is not null
            ? new AgentIdentity(name, instanceId)
            : new AgentIdentity(name);
        return this;
    }

    /// <summary>
    /// Subscribe to a topic.
    /// </summary>
    public AgentHostBuilder SubscribeTo(string topic)
    {
        _options.Topics.Add(topic);
        return this;
    }

    /// <summary>
    /// Register a message type and its handler.
    /// </summary>
    public AgentHostBuilder HandleMessage<TMessage, THandler>(string? messageTypeKey = null)
        where THandler : class, IMessageHandler<TMessage>
    {
        _resolver.Register<TMessage>(messageTypeKey);
        _services.AddScoped<IMessageHandler<TMessage>, THandler>();
        return this;
    }

    /// <summary>
    /// Add middleware to the pipeline. Middleware executes in registration order.
    /// </summary>
    public AgentHostBuilder UseMiddleware<T>() where T : class, IMiddleware
    {
        _services.AddScoped<T>();
        _services.AddSingleton(new MiddlewareRegistration(typeof(T)));
        return this;
    }

    internal void Build()
    {
        _services.AddSingleton(_identity);
        _services.AddSingleton(_resolver);
        _services.AddSingleton<IMessageTypeResolver>(_resolver);
        _services.Configure<AgentHostOptions>(opts =>
        {
            foreach (var topic in _options.Topics)
                opts.Topics.Add(topic);
        });
    }
}
