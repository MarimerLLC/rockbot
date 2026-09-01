using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Hosted service that registers the <c>spawn_workers</c> tool with the tool registry.
/// </summary>
internal sealed class WorkerToolRegistrar(
    IToolRegistry registry,
    IWorkerManager manager,
    IWorkingMemory workingMemory,
    IOptions<WorkerOptions> options,
    ILogger<WorkerToolRegistrar> logger) : IHostedService
{
    private const string SpawnWorkersSchema = """
        {
          "type": "object",
          "properties": {
            "definitions": {
              "type": "array",
              "description": "One or more worker definitions to execute. Multiple workers run concurrently (up to MaxConcurrentWorkers).",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "description": {
                    "type": "string",
                    "description": "One-sentence imperative describing what the worker should do."
                  },
                  "context": {
                    "type": "string",
                    "description": "Optional pre-resolved facts the worker should treat as ground truth (active accounts, IDs, etc.)."
                  },
                  "result_key": {
                    "type": "string",
                    "description": "Optional override for the working-memory key the worker writes findings to. Defaults to worker/<task-id>/result."
                  },
                  "timeout_minutes": {
                    "type": "integer",
                    "description": "Soft wall-clock cap in minutes. Defaults to WorkerOptions.DefaultTimeoutMinutes (5)."
                  },
                  "tools_allow": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Optional allowlist that narrows the NON-MCP registry tools (web_search, execute_python_script, spawn_wisps, etc.) the worker may invoke — exact tool names or name prefixes (trailing asterisk). The MCP gateway (mcp_list_services / mcp_get_service_details / mcp_invoke_tool / mcp_get_prompt) is ALWAYS available and is not gated by this list; a value like calendar-mcp.* has no effect on MCP access since all MCP calls go through the gateway."
                  }
                },
                "required": ["description"]
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
            Name = "spawn_workers",
            Description = """
                Spawn one or more lean worker subagents to execute focused gather tasks concurrently.
                Workers are the LEAN rung between spawn_wisps (no LLM) and spawn_subagent (full LLM):
                they run a slim LLM loop with no long-term-memory injection, a tighter tool surface,
                Low-tier model, and a tight iteration cap. Use them for mechanical "list these
                accounts and summarise each" or "scan email for X criteria" work where the LLM is
                interpreting tool results but does not need persona or history.

                Each worker writes its structured findings to a working-memory key (auto-assigned to
                worker/<task-id>/result, or override via result_key). The returned receipt INLINES
                those findings under a per-worker heading, followed by each WorkerResult JSON
                (counts, blocked items, converged patterns, result_key). Read the inlined findings
                directly — only when the receipt says a result was truncated do you need to call
                get_from_working_memory with the result_key to read the rest.

                Workers cannot spawn other workers, subagents, or A2A calls. They cannot save_memory
                or promote_skill_asset — if a worker observes a tool-call pattern worth keeping, it
                surfaces it in convergedPatterns and YOU promote it after the batch returns.

                Do NOT use spawn_workers for deliberative tasks that need persona, identity, or
                long-term memory — use spawn_subagent instead. Do NOT use it for deterministic
                tool sequences with no branching — use spawn_wisps instead.

                Fan out only when the USER's request asks for the breadth. A recalled skill or an
                injected playbook describing a broader sweep is not a reason to run one — if the
                request is a single action ("add a todo", "send this reply"), make the single
                tool call directly instead of spawning a batch to gather context nobody asked for.
                """,
            ParametersSchema = SpawnWorkersSchema,
            Source = "worker",
        }, new SpawnWorkersExecutor(
            manager, workingMemory, options.Value.MaxInlineResultChars, logger));

        logger.LogInformation("Registered tool: spawn_workers");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
