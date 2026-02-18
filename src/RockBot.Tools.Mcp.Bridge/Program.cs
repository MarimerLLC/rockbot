using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Messaging.RabbitMQ;
using RockBot.Tools.Mcp.Bridge;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRockBotRabbitMq();

builder.Services.Configure<McpBridgeOptions>(
    builder.Configuration.GetSection("McpBridge"));

builder.Services.AddHostedService<McpBridgeService>();

var app = builder.Build();
await app.RunAsync();
