using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools.Web.Brave;

namespace RockBot.Tools.Web;

/// <summary>
/// DI registration extensions for web tools.
/// </summary>
public static class WebServiceCollectionExtensions
{
    /// <summary>
    /// Registers web search and browse tools.
    /// </summary>
    public static AgentHostBuilder AddWebTools(
        this AgentHostBuilder builder,
        Action<WebToolOptions> configure)
    {
        var options = new WebToolOptions();
        configure(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddHttpClient("RockBot.Tools.Web.Brave");
        builder.Services.AddHttpClient("RockBot.Tools.Web.Browse");

        builder.Services.AddSingleton<IWebSearchProvider, BraveSearchProvider>();
        builder.Services.AddSingleton<IWebBrowseProvider, HttpWebBrowseProvider>();

        builder.Services.AddHostedService<WebToolRegistrar>();

        return builder;
    }
}
