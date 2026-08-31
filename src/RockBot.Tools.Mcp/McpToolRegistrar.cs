using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Connects to configured MCP servers on startup and registers their tools.
/// Disposes MCP clients on stop.
/// </summary>
internal sealed class McpToolRegistrar(
    IToolRegistry registry,
    McpOptions options,
    ILogger<McpToolRegistrar> logger) : IHostedService, IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var server in options.Servers)
        {
            try
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = server.Command,
                    Arguments = server.Arguments,
                    EnvironmentVariables = server.EnvironmentVariables!
                });

                var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
                _clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

                foreach (var tool in tools)
                {
                    var toolName = tool.Name;
                    CallToolDelegate callTool = (args, ct) =>
                        client.CallToolAsync(toolName, args, cancellationToken: ct);

                    var executor = new McpToolExecutor(callTool);
                    var registration = new ToolRegistration
                    {
                        Name = tool.Name,
                        Description = tool.Description ?? string.Empty,
                        ParametersSchema = tool.JsonSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined
                            ? tool.JsonSchema.GetRawText()
                            : null,
                        Source = $"mcp:{server.Name}"
                    };

                    registry.Register(registration, executor);
                    logger.LogInformation("Registered MCP tool: {ToolName} from server {ServerName}",
                        tool.Name, server.Name);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to connect to MCP server {ServerName}", server.Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch
            {
                // Best-effort cleanup
            }
        }
        _clients.Clear();
    }
}
