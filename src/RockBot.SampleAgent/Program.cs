using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.Host;
using RockBot.Messaging.RabbitMQ;
using RockBot.SampleAgent;
using RockBot.Tools;
using RockBot.Tools.Mcp;
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
    var openAiClient = new OpenAIClient(
        new ApiKeyCredential(key),
        new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

    builder.Services.AddSingleton<IChatClient>(
        openAiClient.GetChatClient(deploymentName).AsIChatClient());
}
else
{
    builder.Services.AddSingleton<IChatClient, EchoChatClient>();
    Console.WriteLine("No AzureAI config found — using EchoChatClient.");
    Console.WriteLine("Run 'dotnet user-secrets set AzureAI:Endpoint <url>' etc. to configure.");
}

// Register memory tools as singleton — AIFunction instances are built once at construction
builder.Services.AddSingleton<MemoryTools>();
// Tracks which memory IDs have been injected per session, enabling delta recall across topic shifts
builder.Services.AddSingleton<InjectedMemoryTracker>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("sample-agent");
    agent.WithProfile();
    agent.WithMemory();
    agent.WithDreaming(opts =>
    {
        opts.Interval = TimeSpan.FromHours(4);
        opts.InitialDelay = TimeSpan.FromMinutes(5);
    });
    agent.AddToolHandler();
    agent.AddMcpToolProxy();
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.SubscribeTo(UserProxyTopics.UserMessage);
});

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var chatClient = app.Services.GetRequiredService<IChatClient>();
var llmId = chatClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? chatClient.GetType().Name;
startupLogger.LogInformation("LLM: {ModelId}", llmId);
startupLogger.LogInformation("Listening for user messages on '{Topic}'", UserProxyTopics.UserMessage);

await app.RunAsync();
