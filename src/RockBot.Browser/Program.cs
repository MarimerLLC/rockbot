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
builder.Services.AddSingleton<PageRenderer>();
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

await app.RunAsync();
