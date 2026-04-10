using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Hosted service that registers the <c>spawn_wisp</c> tool with the tool registry.
/// </summary>
internal sealed class WispToolRegistrar(
    IToolRegistry registry,
    WispExecutor wispExecutor,
    ILoggerFactory loggerFactory,
    ILogger<WispToolRegistrar> logger,
    IWispExecutionLog? executionLog = null,
    IFeedbackStore? feedbackStore = null) : IHostedService
{
    private const string SpawnWispSchema = """
        {
          "type": "object",
          "properties": {
            "definition": {
              "type": "object",
              "description": "The wisp pipeline definition. Contains 'description' (string), optional 'tools' (string array of additional tool names for LLM steps), and 'steps' (array of step objects). Each step has: 'id' (unique string), 'mode' ('Direct' or 'Llm'), 'gateway' ('Mcp'/'A2A'/'Script'/'Web' for direct steps), plus gateway-specific fields. MCP: 'server', 'tool', 'params'. A2A: 'agent', 'skill', 'message'. Script: 'params' with 'script' field, optional 'language'. Web: 'tool' (web_search/web_browse), 'params'. LLM: 'prompt'. Any step can have 'input_from' (file path or {{steps.id.result}}), 'output_to' (file path), 'on_failure' ({action:'skip_to',skip_to:'step_id'})."
            }
          },
          "required": ["definition"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Register(new ToolRegistration
        {
            Name = "spawn_wisp",
            Description = """
                Execute a lightweight wisp pipeline for procedural multi-step tasks.
                Wisps are harness-supervised pipelines where you provide explicit step-by-step
                instructions. Direct steps invoke tools with zero LLM tokens. LLM steps use
                minimal context. Much cheaper than subagents for procedural tasks.
                Returns structured results with per-step success/failure.
                """,
            ParametersSchema = SpawnWispSchema,
            Source = "wisp"
        }, new SpawnWispExecutor(wispExecutor, executionLog, feedbackStore,
            loggerFactory.CreateLogger<SpawnWispExecutor>()));
        logger.LogInformation("Registered tool: spawn_wisp");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
