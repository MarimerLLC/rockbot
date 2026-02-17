using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Tools.Rest;

/// <summary>
/// DI registration extensions for REST tool backends.
/// </summary>
public static class RestServiceCollectionExtensions
{
    /// <summary>
    /// Registers REST tool endpoints and their registrar.
    /// </summary>
    public static AgentHostBuilder AddRestTools(
        this AgentHostBuilder builder,
        Action<RestToolOptions> configure)
    {
        var options = new RestToolOptions();
        configure(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddHttpClient("RockBot.Tools.Rest");
        builder.Services.AddHostedService<RestToolRegistrar>();

        return builder;
    }
}
