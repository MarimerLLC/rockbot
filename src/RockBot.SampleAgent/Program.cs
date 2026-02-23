using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Messaging.RabbitMQ;
using RockBot.SampleAgent;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddUserSecrets<Program>();

builder.Services.AddRockBotRabbitMq(opts => builder.Configuration.GetSection("RabbitMq").Bind(opts));

// Configure the LLM chat client (same pattern as RockBot.Agent)
var llmConfig = builder.Configuration.GetSection("LLM");
var endpoint = llmConfig["Endpoint"];
var apiKey = llmConfig["ApiKey"];
var modelId = llmConfig["ModelId"];

if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(modelId))
{
    var openAiClient = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

    builder.Services.AddRockBotChatClient(
        openAiClient.GetChatClient(modelId).AsIChatClient());
}
else
{
    builder.Services.AddRockBotChatClient(new EchoChatClient());
    Console.WriteLine("No LLM config found — using EchoChatClient.");
    Console.WriteLine("Set LLM:Endpoint, LLM:ApiKey, and LLM:ModelId to configure.");
}

builder.Services.AddRockBotHost(agent =>
{
    agent.WithIdentity("SampleAgent");

    agent.AddA2A(opts =>
    {
        opts.Card = new AgentCard
        {
            AgentName = "SampleAgent",
            Description = "A sample agent demonstrating the A2A protocol pattern.",
            Version = "1.0",
            Skills =
            [
                new AgentSkill
                {
                    Id = "general",
                    Name = "General Task",
                    Description = "General-purpose task execution using an LLM."
                },
                new AgentSkill
                {
                    Id = "echo",
                    Name = "Echo",
                    Description = "Echoes the input message back as confirmation."
                }
            ]
        };
    });

    agent.Services.AddScoped<IAgentTaskHandler, SampleAgentTaskHandler>();
});

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("SampleAgent starting — listening for A2A task requests");

await app.RunAsync();
