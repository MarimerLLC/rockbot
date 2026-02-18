using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Messaging.RabbitMQ;
using RockBot.Scripts.Bridge;
using RockBot.Scripts.Container;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRockBotRabbitMq();

builder.Services.AddContainerScriptRunner(opts =>
{
    builder.Configuration.GetSection("Scripts:Container").Bind(opts);
});

builder.Services.Configure<ScriptBridgeOptions>(
    builder.Configuration.GetSection("ScriptBridge"));

builder.Services.AddHostedService<ScriptBridgeService>();

var app = builder.Build();
await app.RunAsync();
