using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Host;
using RockBot.Messaging.RabbitMQ;
using RockBot.SampleAgent;
using RockBot.UserProxy;

var builder = Host.CreateApplicationBuilder(args);

// Always load user secrets (CreateApplicationBuilder only loads them in Development)
builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddRockBotRabbitMq();

// Configure the LLM chat client from user secrets / config
var aiConfig = builder.Configuration.GetSection("AzureAI");
var endpoint = aiConfig["Endpoint"];
var key = aiConfig["Key"];
var deploymentName = aiConfig["DeploymentName"];

if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(deploymentName))
{
    var azureClient = new AzureOpenAIClient(
        new Uri(endpoint),
        new System.ClientModel.ApiKeyCredential(key));

    builder.Services.AddSingleton<IChatClient>(
        azureClient.GetChatClient(deploymentName).AsIChatClient());

    Console.WriteLine("Using Azure AI Foundry model: {0}", deploymentName);
}
else
{
    builder.Services.AddSingleton<IChatClient, EchoChatClient>();
    Console.WriteLine("No AzureAI config found — using EchoChatClient.");
    Console.WriteLine("Run 'dotnet user-secrets set AzureAI:Endpoint <url>' etc. to configure.");
}

// Register memory tools as singleton — AIFunction instances are built once at construction
builder.Services.AddSingleton<MemoryTools>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("sample-agent");
    agent.WithProfile();
    agent.WithMemory();
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.SubscribeTo(UserProxyTopics.UserMessage);
});

var app = builder.Build();

Console.WriteLine("Sample agent started. Listening for user messages on '{0}'...",
    UserProxyTopics.UserMessage);
Console.WriteLine("Press Ctrl+C to stop.");

await app.RunAsync();
