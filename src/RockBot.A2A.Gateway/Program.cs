using A2A;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RockBot.A2A.Gateway;
using RockBot.A2A.Gateway.Auth;
using RockBot.Messaging.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.Configure<Dictionary<string, ApiKeyEntry>>(builder.Configuration.GetSection("ApiKeys"));

// ── RabbitMQ ─────────────────────────────────────────────────────────────────

builder.Services.AddRockBotRabbitMq(opts =>
    builder.Configuration.GetSection("RabbitMq").Bind(opts));

// ── Authentication ───────────────────────────────────────────────────────────

builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

// ── A2A v1 server components ─────────────────────────────────────────────────

builder.Services.AddSingleton<ITaskStore, InMemoryTaskStore>();
builder.Services.AddSingleton<ChannelEventNotifier>();
builder.Services.AddSingleton<IAgentHandler, RockBotBridgeHandler>();
builder.Services.AddSingleton(sp => new A2AServer(
    sp.GetRequiredService<IAgentHandler>(),
    sp.GetRequiredService<ITaskStore>(),
    sp.GetRequiredService<ChannelEventNotifier>(),
    sp.GetRequiredService<ILogger<A2AServer>>(),
    new A2AServerOptions()));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// ── Agent card (public — A2A discovery convention) ───────────────────────────

app.MapGet("/.well-known/agent-card.json", (IOptions<GatewayOptions> opts) =>
{
    var config = opts.Value;
    var card = new AgentCard
    {
        Name = config.AgentName,
        Description = config.Description ?? string.Empty,
        Version = config.Version ?? "1.0",
        Skills = config.Skills.Select(s => new AgentSkill
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description ?? string.Empty
        }).ToList(),
        SecuritySchemes = new Dictionary<string, SecurityScheme>
        {
            ["apiKey"] = new SecurityScheme
            {
                ApiKeySecurityScheme = new ApiKeySecurityScheme
                {
                    Name = ApiKeyAuthenticationHandler.HeaderName,
                    Description = "API key for agent authentication",
                    Location = "header"
                }
            }
        },
        SecurityRequirements = [new SecurityRequirement
        {
            Schemes = new Dictionary<string, StringList>
            {
                ["apiKey"] = new StringList()
            }
        }]
    };
    return Results.Json(card);
});

// ── JSON-RPC endpoint (authenticated) ────────────────────────────────────────

app.MapPost("/", (HttpRequest request, A2AServer server, ILoggerFactory loggerFactory) =>
    JsonRpcRouter.HandleAsync(request, server, loggerFactory, request.HttpContext.RequestAborted))
    .RequireAuthorization();

// ── Startup ──────────────────────────────────────────────────────────────────

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("A2A Gateway starting — bridging HTTP A2A to RockBot via RabbitMQ");

await app.RunAsync();
