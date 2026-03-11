using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Hosted service that registers <c>invoke_agent</c>, <c>list_known_agents</c>, and
/// <c>get_agent_details</c> tools with the tool registry.
/// </summary>
internal sealed class A2ACallerToolRegistrar(
    IToolRegistry registry,
    IMessagePublisher publisher,
    IAgentDirectory directory,
    A2ATaskTracker tracker,
    A2AOptions options,
    AgentIdentity identity,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IHostedService
{
    private const string InvokeAgentSchema = """
        {
          "type": "object",
          "properties": {
            "agent_name": {
              "type": "string",
              "description": "The name of the external agent to invoke (e.g. 'SampleAgent')."
            },
            "skill": {
              "type": "string",
              "description": "The skill ID to invoke on the target agent."
            },
            "message": {
              "type": "string",
              "description": "The message or instruction to send to the agent."
            },
            "timeout_minutes": {
              "type": "integer",
              "description": "Optional timeout in minutes (default: 5)."
            }
          },
          "required": ["agent_name", "skill", "message"]
        }
        """;

    private const string ListKnownAgentsSchema = """
        {
          "type": "object",
          "properties": {
            "skill": {
              "type": "string",
              "description": "Optional skill ID to filter agents by."
            }
          }
        }
        """;

    private const string GetAgentDetailsSchema = """
        {
          "type": "object",
          "properties": {
            "agent_name": {
              "type": "string",
              "description": "The name of the agent to retrieve full details for."
            }
          },
          "required": ["agent_name"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var invokeLogger = loggerFactory.CreateLogger<InvokeAgentExecutor>();
        registry.Register(new ToolRegistration
        {
            Name = "invoke_agent",
            Description = """
                Invoke an external A2A agent by name and skill. Dispatches the task asynchronously
                and returns a task_id immediately. The agent's result will arrive as a follow-up
                message in the conversation. Use list_known_agents first to discover available agents.
                """,
            ParametersSchema = InvokeAgentSchema,
            Source = "a2a"
        }, new InvokeAgentExecutor(publisher, tracker, directory, options, identity, httpClientFactory, invokeLogger));

        var registrarLogger = loggerFactory.CreateLogger<A2ACallerToolRegistrar>();
        registrarLogger.LogInformation("Registered tool: invoke_agent");

        registry.Register(new ToolRegistration
        {
            Name = "list_known_agents",
            Description = "List all external A2A agents known to this agent, optionally filtered by skill.",
            ParametersSchema = ListKnownAgentsSchema,
            Source = "a2a"
        }, new ListKnownAgentsExecutor(directory));
        registrarLogger.LogInformation("Registered tool: list_known_agents");

        registry.Register(new ToolRegistration
        {
            Name = "get_agent_details",
            Description = "Get the full agent card for a named agent, including all skill fields (name, description, tags, examples), version, URL, and last-seen time.",
            ParametersSchema = GetAgentDetailsSchema,
            Source = "a2a"
        }, new GetAgentDetailsExecutor(directory));
        registrarLogger.LogInformation("Registered tool: get_agent_details");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
