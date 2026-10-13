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
        builder.Services.AddHttpClient("RockBot.Tools.Web.Browse", client =>
        {
            // Use a realistic browser User-Agent so sites don't reject us as a bot.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        });
        builder.Services.AddHttpClient("RockBot.Tools.Web.GitHub", client =>
        {
            // GitHub API requires a User-Agent header
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RockBot/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        });

        builder.Services.AddHttpClient(RenderServiceWebBrowseProvider.HttpClientName, client =>
        {
            // The service enforces the page budget itself; leave it room to answer.
            client.Timeout = TimeSpan.FromSeconds(options.RenderTimeoutSeconds + 15);
        });

        builder.Services.AddSingleton<IWebSearchProvider, BraveSearchProvider>();
        // Browse chain: GitHub API for issue/PR URLs, then plain HTTP with a headless-browser
        // retry for shells. The inner providers are registered as concrete types so each
        // layer can inject the next directly.
        builder.Services.AddSingleton<HttpWebBrowseProvider>();
        builder.Services.AddSingleton<RenderServiceWebBrowseProvider>();
        builder.Services.AddSingleton<FallbackWebBrowseProvider>();
        builder.Services.AddSingleton<IWebBrowseProvider, GitHubApiWebBrowseProvider>();

        builder.Services.AddSingleton<IToolSkillProvider, WebToolSkillProvider>();

        builder.Services.AddHostedService<WebToolRegistrar>();

        return builder;
    }
}
