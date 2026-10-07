using McpServer.TodoApp;
using McpServer.TodoApp.Services;
using McpServer.TodoApp.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TodoRepository>();
builder.Services.AddHealthChecks();
builder.Services.AddMcpServer(options => options.ServerInstructions = ServerInstructions.Text)
    .WithHttpTransport()
    .WithTools<TodoTools>(ToolJson.Options)
    .WithStrictArguments();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();

public partial class Program;
