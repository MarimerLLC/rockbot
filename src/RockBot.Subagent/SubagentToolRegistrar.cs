using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Subagent;

/// <summary>
/// Hosted service that registers subagent management tools with the tool registry.
/// </summary>
internal sealed class SubagentToolRegistrar(
    IToolRegistry registry,
    ISubagentManager subagentManager,
    ILogger<SubagentToolRegistrar> logger) : IHostedService
{
    private const string SpawnSchema = """
        {
          "type": "object",
          "properties": {
            "description": {
              "type": "string",
              "description": "What the subagent should do. Be specific and detailed."
            },
            "context": {
              "type": "string",
              "description": "Optional additional context or data to provide to the subagent."
            },
            "timeout_minutes": {
              "type": "integer",
              "description": "Optional timeout in minutes (default: 10)."
            }
          },
          "required": ["description"]
        }
        """;

    private const string CancelSchema = """
        {
          "type": "object",
          "properties": {
            "task_id": {
              "type": "string",
              "description": "The task ID of the subagent to cancel."
            }
          },
          "required": ["task_id"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Register(new ToolRegistration
        {
            Name = "spawn_subagent",
            Description = """
                Spawn an isolated subagent to handle a long-running or complex task in the background.
                Returns a task_id immediately. The subagent will report progress via the primary session
                and send a final result when complete. Use this when a task would take many tool calls
                or a long time to complete.
                """,
            ParametersSchema = SpawnSchema,
            Source = "subagent"
        }, new SpawnSubagentExecutor(subagentManager));
        logger.LogInformation("Registered tool: spawn_subagent");

        registry.Register(new ToolRegistration
        {
            Name = "cancel_subagent",
            Description = "Cancel a running subagent task by its task ID.",
            ParametersSchema = CancelSchema,
            Source = "subagent"
        }, new CancelSubagentExecutor(subagentManager));
        logger.LogInformation("Registered tool: cancel_subagent");

        registry.Register(new ToolRegistration
        {
            Name = "list_subagents",
            Description = "List all currently active (running) subagent tasks with their IDs and descriptions.",
            ParametersSchema = null,
            Source = "subagent"
        }, new ListSubagentsExecutor(subagentManager));
        logger.LogInformation("Registered tool: list_subagents");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
