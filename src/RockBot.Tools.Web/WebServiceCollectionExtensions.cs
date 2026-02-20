using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;
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
        builder.Services.AddHttpClient("RockBot.Tools.Web.GitHub", client =>
        {
            // GitHub API requires a User-Agent header
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RockBot/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        });

        builder.Services.AddSingleton<IWebSearchProvider, BraveSearchProvider>();
        // HttpWebBrowseProvider registered as concrete type so GitHubApiWebBrowseProvider
        // can inject it directly as a fallback for non-GitHub URLs.
        builder.Services.AddSingleton<HttpWebBrowseProvider>();
        builder.Services.AddSingleton<IWebBrowseProvider, GitHubApiWebBrowseProvider>();

        builder.Services.AddSingleton<IToolSkillProvider, WebToolSkillProvider>();

        builder.Services.AddHostedService<WebToolRegistrar>();

        return builder;
    }
}
