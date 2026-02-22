using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Messaging.RabbitMQ;
using RockBot.Cli.McpBridge;
using RockBot.Scripts.Remote;
using RockBot.Cli;
using RockBot.Memory;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.A2A;
using RockBot.Subagent;
using RockBot.Tools.Scheduling;
using RockBot.Tools.Web;
using RockBot.UserProxy;

var builder = Host.CreateApplicationBuilder(args);

// Always load user secrets (CreateApplicationBuilder only loads them in Development)
builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

// Configure the LLM chat client.
// Reads from the "LLM" config section (env vars LLM__Endpoint, LLM__ApiKey, LLM__ModelId).
// Any OpenAI-compatible endpoint works — OpenRouter, Azure OpenAI, local Ollama, etc.
var llmConfig = builder.Configuration.GetSection("LLM");
var endpoint = llmConfig["Endpoint"];
var apiKey = llmConfig["ApiKey"];
var modelId = llmConfig["ModelId"];

if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(modelId))
{
    var openAiClient = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            // Extend from the 100s default — subagents with large tool sets generate
            // longer responses that can exceed the default before the body is fully read.
            NetworkTimeout = TimeSpan.FromMinutes(5)
        });

    builder.Services.AddSingleton<IChatClient>(
        openAiClient.GetChatClient(modelId).AsIChatClient());
}
else
{
    builder.Services.AddSingleton<IChatClient, EchoChatClient>();
    Console.WriteLine("No LLM config found — using EchoChatClient.");
    Console.WriteLine("Set LLM:Endpoint, LLM:ApiKey, and LLM:ModelId to configure.");
}

// Tracks in-flight background tool loops so they can be cancelled when a new message arrives
builder.Services.AddSingleton<SessionBackgroundTaskTracker>();

// Register memory tools as singleton — AIFunction instances are built once at construction
builder.Services.AddSingleton<MemoryTools>();
// Rules tools — requires WithRules() in the agent builder below
builder.Services.AddSingleton<RulesTools>();
// Tracks which memory IDs have been injected per session, enabling delta recall across topic shifts
builder.Services.AddSingleton<InjectedMemoryTracker>();
// Skill index tracker (SkillTools is created per-session in UserMessageHandler)
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
    agent.WithConversationLog();
    agent.WithFeedback();
    agent.WithSkills();
    agent.WithDreaming(opts =>
    {
        opts.Interval = TimeSpan.FromHours(4);
        opts.InitialDelay = TimeSpan.FromMinutes(5);
    });
    agent.AddToolHandler();
    agent.AddMcpToolProxy();
    agent.AddWebTools(opts => builder.Configuration.GetSection("WebTools").Bind(opts));
    agent.AddSchedulingTools();
    agent.AddSubagents();
    agent.AddA2ACaller(opts =>
    {
        var basePath = builder.Configuration["AgentProfile:BasePath"]
            ?? builder.Configuration["AgentProfile__BasePath"]
            ?? AppContext.BaseDirectory;
        opts.DirectoryPersistencePath = Path.Combine(basePath, "known-agents.json");
    });
    agent.HandleMessage<ScheduledTaskMessage, ScheduledTaskHandler>();
    agent.HandleMessage<UserMessage, UserMessageHandler>();
    agent.HandleMessage<ConversationHistoryRequest, ConversationHistoryRequestHandler>();
    agent.SubscribeTo(UserProxyTopics.UserMessage);
    agent.SubscribeTo(UserProxyTopics.ConversationHistoryRequest);
});

// Per-model behavioral tweaks — resolved once from the configured IChatClient model ID.
builder.Services.AddModelBehaviors(opts =>
    builder.Configuration.GetSection("ModelBehaviors").Bind(opts));

// Bind AgentProfileOptions from the AgentProfile config section so AgentProfile__BasePath
// (set in the k8s ConfigMap) overrides the default "agent" relative path → /data/agent (PVC).
builder.Services.Configure<AgentProfileOptions>(builder.Configuration.GetSection("AgentProfile"));

// MCP bridge (replaces external RockBot.Tools.Mcp.Bridge process)
builder.Services.Configure<McpBridgeOptions>(builder.Configuration.GetSection("McpBridge"));
builder.Services.AddHostedService<McpBridgeService>();

// Remote script runner — delegates script execution to the Script Manager pod via RabbitMQ
builder.Services.AddRemoteScriptRunner("RockBot");

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var chatClient = app.Services.GetRequiredService<IChatClient>();
var llmId = chatClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? chatClient.GetType().Name;
startupLogger.LogInformation("LLM: {ModelId}", llmId);
var resolvedBehavior = app.Services.GetRequiredService<ModelBehavior>();
startupLogger.LogInformation(
    "ModelBehavior: NudgeOnHallucinatedToolCalls={Nudge}, AdditionalSystemPrompt={HasPrompt}, ScheduledTaskResultMode={ResultMode}",
    resolvedBehavior.NudgeOnHallucinatedToolCalls,
    resolvedBehavior.AdditionalSystemPrompt is not null ? "yes" : "no",
    resolvedBehavior.ScheduledTaskResultMode);
startupLogger.LogInformation("Listening for user messages on '{Topic}'", UserProxyTopics.UserMessage);

await app.RunAsync();
