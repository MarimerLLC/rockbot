using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.ServiceSearch;

/// <summary>
/// Hosted service that registers the <c>search_known_services</c> tool with the tool registry.
/// </summary>
internal sealed class ServiceSearchToolRegistrar(
    IToolRegistry registry,
    IServiceSearchIndex searchIndex,
    ILogger<ServiceSearchToolRegistrar> logger) : IHostedService
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Keywords describing the task or capability (e.g., 'reschedule meeting', 'edit python script', 'lookup customer crm')."
            }
          },
          "required": ["query"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Register(new ToolRegistration
        {
            Name = "search_known_services",
            Description = """
                BM25 keyword search across all known A2A agents and MCP servers.
                Returns a ranked list of candidates with summaries and top tools/skills to help
                identify the right service for a task before calling mcp_get_service_details or get_agent_details.
                Result 'type' field determines how to interact: 'mcp' → mcp_invoke_tool, 'a2a' → invoke_agent.
                """,
            ParametersSchema = Schema,
            Source = "service-search"
        }, new SearchKnownServicesExecutor(searchIndex));

        logger.LogInformation("Registered tool: search_known_services");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
