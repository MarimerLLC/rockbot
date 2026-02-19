using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RockBot.Host;
using RockBot.Messaging.RabbitMQ;
using RockBot.Scripts.Container;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRockBotRabbitMq(opts =>
    builder.Configuration.GetSection("RabbitMq").Bind(opts));

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("scripts-manager");
    agent.AddContainerScriptHandler(opts =>
        builder.Configuration.GetSection("Scripts:Container").Bind(opts));
});

var app = builder.Build();
await app.RunAsync();
