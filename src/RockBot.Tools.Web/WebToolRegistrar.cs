using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Tools.Web;

internal sealed class WebToolRegistrar(
    IToolRegistry registry,
    IWebSearchProvider searchProvider,
    IWebBrowseProvider browseProvider,
    IWorkingMemory workingMemory,
    WebToolOptions options,
    ILogger<WebToolRegistrar> logger) : IHostedService
{
    private const string SearchSchema = """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "The search query"
            },
            "count": {
              "type": "integer",
              "description": "Number of results to return (1-20, default 10)",
              "minimum": 1,
              "maximum": 20
            }
          },
          "required": ["query"]
        }
        """;

    private const string BrowseSchema = """
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "description": "The URL of the web page to fetch"
            }
          },
          "required": ["url"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Register(new ToolRegistration
        {
            Name = "web_search",
            Description = "Search the web and return titles, URLs, and snippets",
            ParametersSchema = SearchSchema,
            Source = "web"
        }, new WebSearchToolExecutor(searchProvider, options));
        logger.LogInformation("Registered web tool: web_search");

        registry.Register(new ToolRegistration
        {
            Name = "web_browse",
            Description = "Fetch a web page and return its content as Markdown",
            ParametersSchema = BrowseSchema,
            Source = "web"
        }, new WebBrowseToolExecutor(browseProvider, workingMemory, options));
        logger.LogInformation("Registered web tool: web_browse");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
