using RockBot.Browser;

// Headless-browser render service. Runs as a sidecar in the agent pod; the agent's
// web_browse tool calls POST /render when a plain HTTP fetch comes back as a consent wall
// or a JavaScript shell. Anonymous by design: it holds no cookies or credentials.
var builder = WebApplication.CreateBuilder(args);

var options = new BrowserOptions();
builder.Configuration.GetSection("Browser").Bind(options);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<UrlGuard>();
builder.Services.AddSingleton<BrowserHost>();
builder.Services.AddSingleton<GuardedContext>();
builder.Services.AddSingleton<PageRenderer>();
builder.Services.AddSingleton<PageScreenshotter>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapPost("/render", async (RenderRequest request, PageRenderer renderer, CancellationToken ct) =>
{
    var result = await renderer.RenderAsync(request, ct);
    return result.Error is null
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status502BadGateway);
});

// The agent's render_screenshot tool posts an HTML document it wrote (a deck, a report page)
// and gets back a PNG of the first viewport, so it can check its own output. The document is
// sent as text; this service has no access to the agent's files.
app.MapPost("/screenshot", async (ScreenshotRequest request, PageScreenshotter screenshotter, HttpContext http, CancellationToken ct) =>
{
    var result = await screenshotter.CaptureAsync(request, ct);
    if (result.Png is null)
    {
        return Results.Json(
            new ScreenshotError(result.Error ?? "The screenshot failed."),
            statusCode: result.BadRequest ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);
    }

    http.Response.Headers[ScreenshotHeaders.BlockedRequests] = result.BlockedRequests.ToString();
    return Results.Bytes(result.Png, "image/png");
});

await app.RunAsync();
