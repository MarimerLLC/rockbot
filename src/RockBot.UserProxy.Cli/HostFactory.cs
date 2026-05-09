using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging.RabbitMQ;

namespace RockBot.UserProxy.Cli;

/// <summary>
/// Builds the host used by every CLI subcommand. CLI flags from
/// <see cref="CommonSettings"/> are layered as the highest-priority
/// configuration source so they override env vars / appsettings / user secrets.
/// </summary>
internal static class HostFactory
{
    public static IHost Build(CommonSettings settings, bool useRichFrontend)
    {
        // Pass no args — CLI flags are already parsed by Spectre and applied
        // below as in-memory config; we don't want Microsoft's CommandLine
        // provider to try to interpret them again.
        var builder = Host.CreateApplicationBuilder();

        // Match the agent: load user secrets unconditionally so the same
        // dotnet user-secrets workflow used elsewhere works for the CLI too.
        builder.Configuration.AddUserSecrets<HostFactoryMarker>(optional: true);

        var overrides = new Dictionary<string, string?>();
        if (settings.RabbitMqHost is not null) overrides["RabbitMq:HostName"] = settings.RabbitMqHost;
        if (settings.RabbitMqPort is not null) overrides["RabbitMq:Port"] = settings.RabbitMqPort.Value.ToString();
        if (settings.RabbitMqUser is not null) overrides["RabbitMq:UserName"] = settings.RabbitMqUser;
        if (settings.RabbitMqPassword is not null) overrides["RabbitMq:Password"] = settings.RabbitMqPassword;
        if (settings.AgentName is not null) overrides["Agent:Name"] = settings.AgentName;
        if (overrides.Count > 0)
            builder.Configuration.AddInMemoryCollection(overrides);

        if (!settings.Verbose)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Warning);
        }

        builder.Services.AddRockBotRabbitMq(opts =>
            builder.Configuration.GetSection("RabbitMq").Bind(opts));

        // Default to a unique ProxyId so concurrent UserProxy clients (e.g., the
        // K8s Blazor pod) don't share our response queues — RabbitMQ round-robins
        // queue consumers, which would silently steal our correlated replies.
        // Pass --proxy-id explicitly to opt back into a shared identity.
        var proxyId = settings.ProxyId
            ?? builder.Configuration["UserProxy:ProxyId"]
            ?? $"cli-{Guid.NewGuid():N}";

        builder.Services.AddUserProxy(opts =>
        {
            opts.AgentName = builder.Configuration["Agent:Name"] ?? "RockBot";
            opts.ProxyId = proxyId;
            if (settings.TimeoutSeconds is { } t)
                opts.DefaultReplyTimeout = TimeSpan.FromSeconds(t);
        });

        if (useRichFrontend)
            builder.Services.AddSingleton<IUserFrontend, SpectreConsoleFrontend>();
        else
            builder.Services.AddSingleton<IUserFrontend, PlainConsoleFrontend>();

        return builder.Build();
    }

    /// <summary>Type used as the user-secrets identity marker for the CLI.</summary>
    private sealed class HostFactoryMarker { }
}
