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
    InputRequiredHandler inputRequiredHandler,
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
            "data": {
              "type": "object",
              "description": "Optional structured data payload sent alongside the text message as an A2A DataPart. Include it whenever the request has structured inputs — identifiers, records, parameters, filters, coordinates, enums, etc. — rather than stuffing them into the message text. Inspect the target skill's description, tags, and examples (via get_agent_details) for field hints. If the skill doesn't document structured fields, send the values in message text instead — don't fabricate field names. Must be a JSON object."
            },
            "metadata": {
              "type": "object",
              "description": "Optional A2A message metadata — per-skill control parameters that the target agent advertises in its skill description (e.g. 'providerId', 'count', 'since'). Send filter/control values here when the skill description documents them as 'metadata parameters' or 'metadata keys'. Values must be primitives (string, number, boolean) or ISO-8601 strings — no nested objects or arrays. Distinct from 'data': metadata is for routing/filter hints; data is for the request body. Must be a JSON object."
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

    private const string RegisterAgentSchema = """
        {
          "type": "object",
          "properties": {
            "agent_name": {
              "type": "string",
              "description": "A unique name for the agent."
            },
            "url": {
              "type": "string",
              "description": "Base URL for the agent's HTTP endpoint (e.g. 'https://api.example.com')."
            },
            "description": {
              "type": "string",
              "description": "Human-readable description of the agent's capabilities."
            },
            "skills": {
              "type": "array",
              "description": "List of skills the agent supports.",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "name": { "type": "string" },
                  "description": { "type": "string" }
                },
                "required": ["id", "name"]
              }
            },
            "auth_header_name": {
              "type": "string",
              "description": "HTTP header name for authentication (e.g. 'Authorization', 'X-Api-Key'). Must be provided with auth_header_value_base64."
            },
            "auth_header_value_base64": {
              "type": "string",
              "description": "Base64-encoded value for the auth header (e.g. base64 of 'Bearer sk-...'). Must be provided with auth_header_name."
            },
            "protocol_version": {
              "type": "string",
              "description": "A2A protocol version (e.g. '0.3', '1.0'). If omitted, auto-detected from the agent's well-known card endpoint."
            }
          },
          "required": ["agent_name", "url"]
        }
        """;

    private const string UnregisterAgentSchema = """
        {
          "type": "object",
          "properties": {
            "agent_name": {
              "type": "string",
              "description": "The name of the agent to remove from the directory."
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
        }, new InvokeAgentExecutor(publisher, tracker, directory, options, identity, httpClientFactory, inputRequiredHandler, invokeLogger));

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

        registry.Register(new ToolRegistration
        {
            Name = "register_agent",
            Description = "Register or update an HTTP-based A2A agent in the directory. " +
                          "Supports optional auth header for agents requiring API keys. " +
                          "The agent is persisted and available for invoke_agent immediately.",
            ParametersSchema = RegisterAgentSchema,
            Source = "a2a"
        }, new RegisterAgentExecutor(directory, httpClientFactory, loggerFactory.CreateLogger<RegisterAgentExecutor>()));
        registrarLogger.LogInformation("Registered tool: register_agent");

        registry.Register(new ToolRegistration
        {
            Name = "unregister_agent",
            Description = "Remove an agent from the directory. Well-known agents (statically configured) cannot be removed.",
            ParametersSchema = UnregisterAgentSchema,
            Source = "a2a"
        }, new UnregisterAgentExecutor(directory, loggerFactory.CreateLogger<UnregisterAgentExecutor>()));
        registrarLogger.LogInformation("Registered tool: unregister_agent");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
