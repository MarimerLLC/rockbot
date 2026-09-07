using McpServer.BinaryFixture.Fixtures;
using McpServer.BinaryFixture.Tools;

// Fixture MCP server for exercising the bridge's binary-content paths against a live agent.
// Not part of any normal deployment — see deploy/k8s/mcp-binary-fixture.yaml and the README.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<BinaryFixtureTools>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

// The fixtures over plain HTTP as well as MCP. When a live test disagrees with what a model
// says it saw, the question is always "what was actually in the file" — these answer it without
// having to reproduce the MCP call.
app.MapGet("/fixtures/chart.png", () => Results.File(TestMedia.BarChartPng, "image/png"));
app.MapGet("/fixtures/tone.wav", () => Results.File(TestMedia.ToneWav, "audio/wav"));
app.MapGet("/fixtures/expected", () => Results.Text(TestMedia.BarChartDescription));

await app.RunAsync();
