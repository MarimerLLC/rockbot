using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Hosted service that registers the <c>spawn_wisps</c> tool with the tool registry.
/// </summary>
internal sealed class WispToolRegistrar(
    IToolRegistry registry,
    WispExecutor wispExecutor,
    IWorkingMemory workingMemory,
    WispOptions options,
    ILoggerFactory loggerFactory,
    ILogger<WispToolRegistrar> logger,
    IWispExecutionLog? executionLog = null,
    IFeedbackStore? feedbackStore = null) : IHostedService
{
    private const string SpawnWispsSchema = """
        {
          "type": "object",
          "properties": {
            "definitions": {
              "type": "array",
              "description": "Array of wisp pipeline definitions to execute concurrently. Each definition contains 'description' (string), optional 'tools' (string array of additional tool names for LLM steps), and 'steps' (array of step objects). Each step has: 'id' (unique string), 'mode' ('Direct' or 'Llm'), 'gateway' ('Mcp'/'A2A'/'Script'/'Web' for direct steps), plus gateway-specific fields. MCP: 'server', 'tool', 'params'. A2A: 'agent', 'skill', 'message'. Script: 'params' with 'script' field, optional 'language'. Web: 'tool' (web_search/web_browse), 'params'. LLM: 'prompt'. Any step can have 'input_from' (file path or {{steps.id.result}}), 'output_to' (file path), 'on_failure' ({action:'skip_to',skip_to:'step_id'}).",
              "items": { "type": "object" },
              "minItems": 1
            }
          },
          "required": ["definitions"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Register(new ToolRegistration
        {
            Name = "spawn_wisps",
            Description = """
                Execute one or more lightweight wisp pipelines. Multiple wisps run concurrently
                (up to the configured limit). Each wisp is a harness-supervised pipeline where
                you provide explicit step-by-step instructions. Direct steps invoke tools with
                zero LLM tokens. LLM steps use minimal context. Much cheaper than subagents for
                procedural tasks. Returns a batch result with per-wisp success/failure and writes
                a summary to working memory.
                """,
            ParametersSchema = SpawnWispsSchema,
            Source = "wisp"
        }, new SpawnWispsExecutor(wispExecutor, executionLog, feedbackStore, workingMemory, options,
            loggerFactory.CreateLogger<SpawnWispsExecutor>()));
        logger.LogInformation("Registered tool: spawn_wisps");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
