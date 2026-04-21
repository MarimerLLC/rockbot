using RockBot.A2A.Gateway;
using RockBot.A2A.Gateway.Auth;
using RockBot.Messaging.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<Dictionary<string, ApiKeyEntry>>(
    builder.Configuration.GetSection("ApiKeys"));

builder.Services.AddRockBotRabbitMq(opts =>
    builder.Configuration.GetSection("RabbitMq").Bind(opts));

builder.Services.AddA2AApiKeyAuthentication();

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
