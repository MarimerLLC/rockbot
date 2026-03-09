using McpServer.RoutingStats.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<RoutingStatsTools>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();
