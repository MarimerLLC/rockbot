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
    IHttpClientFactory httpClientFactory,
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

    private const string ScreenshotSchema = """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Shared-volume path of the .html file to capture, e.g. 'exports/q3-review.html'"
            },
            "output_path": {
              "type": "string",
              "description": "Where to save the PNG on the shared volume. Defaults to tmp/screenshots/<name>-<timestamp>.png"
            },
            "width": {
              "type": "integer",
              "description": "Viewport width in pixels (default 1280)"
            },
            "height": {
              "type": "integer",
              "description": "Viewport height in pixels (default 720)"
            }
          },
          "required": ["path"]
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

        // Only with a render service: without one there is no browser to take the picture.
        if (!string.IsNullOrWhiteSpace(options.RenderServiceUrl))
        {
            registry.Register(new ToolRegistration
            {
                Name = RenderScreenshotToolExecutor.ToolName,
                Description = """
                    Take a screenshot of an HTML file you wrote on the shared volume (a slide deck,
                    a report page) as it renders in a real browser, and save it as a PNG on the
                    shared volume. Returns the PNG's path; you have not seen the image until you
                    call analyze_file on that path. Use it to check your own output — layout,
                    overflow, missing styles — before telling the user it is ready. Captures the
                    first viewport only (the title slide of a deck). The file must be
                    self-contained: one .html file, with any libraries, fonts or images loaded
                    from public URLs or inlined. Not for web pages: use web_browse for those.
                    """,
                ParametersSchema = ScreenshotSchema,
                Source = "web"
            }, new RenderScreenshotToolExecutor(httpClientFactory, options));
            logger.LogInformation("Registered web tool: {Tool}", RenderScreenshotToolExecutor.ToolName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
