using McpServer.ElicitationFixture.Tools;

// Fixture MCP server for exercising elicitation hand-back (and the chat's question form) against
// a live agent. Not part of any normal deployment — see the README and
// deploy/k8s/mcp-elicitation-fixture.yaml.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<ElicitationFixtureTools>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();
