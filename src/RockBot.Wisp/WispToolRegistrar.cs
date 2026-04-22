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
              "description": "One or more wisp pipeline definitions to execute. Multiple definitions run concurrently.",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "description": {
                    "type": "string",
                    "description": "Human-readable description of what this pipeline does."
                  },
                  "tools": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Optional additional tool names available to LLM steps (e.g. web_browse)."
                  },
                  "steps": {
                    "type": "array",
                    "description": "Ordered steps to execute. Each step runs sequentially within this wisp.",
                    "minItems": 1,
                    "items": {
                      "type": "object",
                      "properties": {
                        "id": { "type": "string", "description": "Unique step identifier." },
                        "mode": { "type": "string", "enum": ["Direct", "Llm"], "description": "Direct = harness calls tool (zero LLM tokens). Llm = lightweight LLM interprets prompt." },
                        "gateway": { "type": "string", "enum": ["Mcp", "A2A", "Script", "Web"], "description": "Tool backend for Direct steps." },
                        "server": { "type": "string", "description": "MCP server name (gateway=Mcp)." },
                        "tool": { "type": "string", "description": "Tool name (gateway=Mcp or Web)." },
                        "params": { "type": "object", "description": "Tool parameters as key-value pairs." },
                        "prompt": { "type": "string", "description": "Prompt for LLM steps (mode=Llm). Must be self-contained." },
                        "agent": { "type": "string", "description": "A2A agent name (gateway=A2A)." },
                        "skill": { "type": "string", "description": "A2A skill ID (gateway=A2A)." },
                        "message": { "type": "string", "description": "A2A message content (gateway=A2A)." },
                        "input_from": { "type": "string", "description": "File path or {{steps.id.result}} template for step input." },
                        "output_to": { "type": "string", "description": "File path to write step output." },
                        "on_failure": {
                          "type": "object",
                          "properties": {
                            "action": { "type": "string", "enum": ["abort", "skip_to"] },
                            "skip_to": { "type": "string", "description": "Step ID to jump to on failure." }
                          }
                        }
                      },
                      "required": ["id", "mode"]
                    }
                  }
                },
                "required": ["description", "steps"]
              }
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

                Before authoring a wisp that targets an MCP server, call
                mcp_get_service_details(server_name=...) once so you have the exact parameter
                schema for each tool in context. Authoring from training priors is where wisps
                most often go wrong — parameter names (e.g. `timeMin` vs `startDate`) vary by
                server and cannot be guessed reliably.

                Reuse before authoring: if a skill you've loaded has a Wisp resource listed in
                its manifest, fetch it with get_skill_resource and start from that definition
                rather than composing from scratch. Conversely, once you've authored a wisp
                that works — especially after debugging through a failure — save it back as
                a Wisp resource on the relevant skill so future sessions can reuse it.
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
