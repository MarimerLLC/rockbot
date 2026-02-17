using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Host;
using RockBot.Messaging.RabbitMQ;
using RockBot.SampleAgent;
using RockBot.UserProxy;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddRockBotRabbitMq();

// Use EchoChatClient by default. Replace with a real LLM provider:
//   builder.Services.AddSingleton<IChatClient>(new OpenAIChatClient("gpt-4o", apiKey));
builder.Services.AddSingleton<IChatClient, EchoChatClient>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("sample-agent");
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.SubscribeTo(UserProxyTopics.UserMessage);
});

var app = builder.Build();

Console.WriteLine("Sample agent started. Listening for user messages on '{0}'...",
    UserProxyTopics.UserMessage);
Console.WriteLine("Press Ctrl+C to stop.");

await app.RunAsync();
