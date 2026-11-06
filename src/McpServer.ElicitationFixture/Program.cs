using McpServer.ElicitationFixture.Tools;
using McpServer.ElicitationFixture.Watches;

// Fixture MCP server for exercising elicitation hand-back (and the chat's question form), and
// resource watches over subscriptions/listen, against a live agent. Not part of any normal
// deployment — see the README and deploy/k8s/mcp-elicitation-fixture.yaml.
var builder = WebApplication.CreateBuilder(args);

var watchable = new WatchableResources();
if (int.TryParse(builder.Configuration["Fixture:StreamMinutes"], out var streamMinutes) && streamMinutes > 0)
    watchable.StreamLifetime = TimeSpan.FromMinutes(streamMinutes);

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer(watchable.Configure)
    .WithHttpTransport()
    .WithTools<ElicitationFixtureTools>()
    .WithTools(watchable.Tools())
    .WithResources(watchable.Resources());

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();
