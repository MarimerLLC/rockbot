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
            },
            "render": {
              "type": "boolean",
              "description": "Render the page in a headless browser first. Use for pages you know build their content with JavaScript; otherwise leave it off — shells are retried in the browser automatically."
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
            Description = """
                Fetch a web page and return its content as Markdown.
                Large pages are automatically split into chunks stored in working memory.
                When that happens the tool returns a chunk index table (heading + key per chunk)
                instead of the full content. You MUST then call get_from_working_memory(key) for
                each chunk you need — read the heading column first to identify relevant sections,
                then retrieve only those before answering. Do not report on the page until you
                have read the chunks that contain the answer.
                The result starts with a Source line. If it also carries a
                "[web_browse status: ...]" line, the page body was NOT read (consent wall,
                JavaScript-only page, or empty response): say so, and do not present facts
                about that page as confirmed.
                """,
            ParametersSchema = BrowseSchema,
            Source = "web"
        }, new WebBrowseToolExecutor(browseProvider, workingMemory, options));
        logger.LogInformation("Registered web tool: web_browse");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
