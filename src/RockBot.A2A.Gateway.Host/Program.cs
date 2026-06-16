using RockBot.A2A.Gateway;
using RockBot.A2A.Gateway.Auth;
using RockBot.Messaging.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<Dictionary<string, ApiKeyEntry>>(
    builder.Configuration.GetSection("ApiKeys"));

// JWT/Bearer (generic OIDC) options — bound for the agent-card endpoint and the auth scheme.
var jwtOptions = new JwtAuthOptions();
builder.Configuration.GetSection("Jwt").Bind(jwtOptions);
builder.Services.Configure<JwtAuthOptions>(builder.Configuration.GetSection("Jwt"));

builder.Services.AddRockBotRabbitMq(opts =>
    builder.Configuration.GetSection("RabbitMq").Bind(opts));

// API-key auth is always on; Bearer is added when an OIDC Authority is configured.
builder.Services.AddA2AApiKeyAuthentication()
    .AddA2AJwtBearerAuthentication(jwtOptions);

builder.Services.AddA2AHttpGateway(opts =>
    builder.Configuration.GetSection("Gateway").Bind(opts));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapA2AHttpGateway();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("A2A Gateway starting — bridging HTTP A2A to RockBot via RabbitMQ");

await app.RunAsync();

// Exposed so WebApplicationFactory<Program> in RockBot.A2A.Gateway.Tests can target this host.
public partial class Program { }
