using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.Host;
using RockBot.Messaging.RabbitMQ;
using RockBot.Cli.McpBridge;
using RockBot.Cli.ScriptBridge;
using RockBot.Scripts.Container;
using RockBot.Cli;
using RockBot.Memory;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Web;
using RockBot.UserProxy;

var builder = Host.CreateApplicationBuilder(args);

// Always load user secrets (CreateApplicationBuilder only loads them in Development)
builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

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
// Rules tools — requires WithRules() in the agent builder below
builder.Services.AddSingleton<RulesTools>();
// Tracks which memory IDs have been injected per session, enabling delta recall across topic shifts
builder.Services.AddSingleton<InjectedMemoryTracker>();
// Skill tools and session index tracker
builder.Services.AddSingleton<SkillTools>();
builder.Services.AddSingleton<SkillIndexTracker>();
builder.Services.AddSingleton<SkillRecallTracker>();
// Tool guides for memory and skill subsystems
builder.Services.AddSingleton<IToolSkillProvider, MemoryToolSkillProvider>();
builder.Services.AddSingleton<IToolSkillProvider, SkillToolSkillProvider>();

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("RockBot");
    agent.WithProfile();
    agent.WithRules();
    agent.WithMemory();
    agent.WithSkills();
    agent.WithDreaming(opts =>
    {
        opts.Interval = TimeSpan.FromHours(4);
        opts.InitialDelay = TimeSpan.FromMinutes(5);
    });
    agent.AddToolHandler();
    agent.AddMcpToolProxy();
    agent.AddWebTools(opts => builder.Configuration.GetSection("WebTools").Bind(opts));
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.SubscribeTo(UserProxyTopics.UserMessage);
});

// MCP bridge (replaces external RockBot.Tools.Mcp.Bridge process)
builder.Services.Configure<McpBridgeOptions>(builder.Configuration.GetSection("McpBridge"));
builder.Services.AddHostedService<McpBridgeService>();

// Script bridge (replaces external RockBot.Scripts.Bridge process)
builder.Services.AddContainerScriptRunner(opts =>
    builder.Configuration.GetSection("Scripts:Container").Bind(opts));
builder.Services.Configure<ScriptBridgeOptions>(builder.Configuration.GetSection("ScriptBridge"));
builder.Services.AddHostedService<ScriptBridgeService>();

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var chatClient = app.Services.GetRequiredService<IChatClient>();
var llmId = chatClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? chatClient.GetType().Name;
startupLogger.LogInformation("LLM: {ModelId}", llmId);
startupLogger.LogInformation("Listening for user messages on '{Topic}'", UserProxyTopics.UserMessage);

await app.RunAsync();
