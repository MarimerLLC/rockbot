using McpServer.OpenRouter.Options;
using McpServer.OpenRouter.Services;
using McpServer.OpenRouter.Tools;

var builder = WebApplication.CreateBuilder(args);

// Always load user secrets regardless of environment (for local dev convenience).
builder.Configuration.AddUserSecrets<Program>(optional: true);

// Bind OpenRouter options — API key comes from user secrets, env var, or k8s secret.
// Never store the key in appsettings.json or source control.
builder.Services.Configure<OpenRouterOptions>(
    builder.Configuration.GetSection(OpenRouterOptions.SectionName));

// Register the OpenRouter HTTP client.
builder.Services.AddHttpClient<OpenRouterClient>();

// Register health checks.
builder.Services.AddHealthChecks();

// Register the MCP server with HTTP (streamable HTTP) transport and our tools.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<OpenRouterTools>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();
