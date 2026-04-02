using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RockBot.Host;
using RockBot.Messaging.RabbitMQ;
using RockBot.Scripts.Container;
using RockBot.Scripts.Docker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRockBotRabbitMq(opts =>
    builder.Configuration.GetSection("RabbitMq").Bind(opts));

var provider = builder.Configuration.GetValue<string>("Scripts:Provider") ?? "Container";

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("scripts-manager");

    if (string.Equals(provider, "Docker", StringComparison.OrdinalIgnoreCase))
        agent.AddDockerScriptHandler(opts =>
            builder.Configuration.GetSection("Scripts:Docker").Bind(opts));
    else
        agent.AddContainerScriptHandler(opts =>
            builder.Configuration.GetSection("Scripts:Container").Bind(opts));
});

var app = builder.Build();
await app.RunAsync();
