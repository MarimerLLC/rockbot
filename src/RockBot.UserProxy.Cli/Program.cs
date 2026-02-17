using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Messaging.RabbitMQ;
using RockBot.UserProxy;
using RockBot.UserProxy.Cli;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRockBotRabbitMq();
builder.Services.AddUserProxy();
builder.Services.AddSingleton<IUserFrontend, SpectreConsoleFrontend>();
builder.Services.AddHostedService<ChatLoopService>();

var app = builder.Build();
await app.RunAsync();
