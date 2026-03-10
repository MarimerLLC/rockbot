using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using RockBot.A2A;
using RockBot.SampleAgent.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>();

// Configure the LLM chat client
var llmConfig = builder.Configuration.GetSection("LLM");
var endpoint = llmConfig["Endpoint"];
var apiKey = llmConfig["ApiKey"];
var modelId = llmConfig["ModelId"];

if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(modelId))
{
    var openAiClient = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

    builder.Services.AddSingleton<IChatClient>(
        openAiClient.GetChatClient(modelId).AsIChatClient());
}
else
{
    builder.Services.AddSingleton<IChatClient>(new EchoChatClient());
    Console.WriteLine("No LLM config found — using EchoChatClient.");
    Console.WriteLine("Set LLM:Endpoint, LLM:ApiKey, and LLM:ModelId to configure.");
}

builder.Services.AddScoped<SampleAgentHttpTaskHandler>();

var agentCard = new AgentCard
{
    AgentName = "SampleAgent-Http",
    Description = "A sample agent demonstrating the A2A protocol pattern over HTTP.",
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

var app = builder.Build();

// GET /.well-known/agent.json — returns the agent card so callers can discover capabilities
app.MapGet("/.well-known/agent.json", () => Results.Ok(agentCard))
    .WithName("GetAgentCard");

// POST /tasks/send — accepts an AgentTaskRequest, processes it, returns AgentTaskResult
app.MapPost("/tasks/send", async (
    AgentTaskRequest request,
    SampleAgentHttpTaskHandler handler,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    logger.LogInformation("Received task {TaskId} (skill={Skill})", request.TaskId, request.Skill);

    var result = await handler.HandleTaskAsync(request, ct);

    logger.LogInformation("Completed task {TaskId} (state={State})", request.TaskId, result.State);
    return Results.Ok(result);
});

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation("SampleAgent-Http starting — listening for HTTP A2A task requests");

await app.RunAsync();
