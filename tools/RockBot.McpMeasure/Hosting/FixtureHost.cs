using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RockBot.McpMeasure.Fixtures;

namespace RockBot.McpMeasure.Hosting;

/// <summary>
/// Runs each fixture server as a real MCP server over streamable HTTP on loopback, so the bridge
/// connects to it exactly as it connects to a production server.
/// </summary>
public sealed class FixtureHost : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];

    private FixtureHost(CallRecorder recorder) => Recorder = recorder;

    public CallRecorder Recorder { get; }

    /// <summary>Server name → MCP endpoint URL.</summary>
    public Dictionary<string, string> Urls { get; } = new(StringComparer.Ordinal);

    /// <summary>The servers, with the tool schemas the model sees.</summary>
    public IReadOnlyList<FixtureServer> Servers { get; private set; } = [];

    public static async Task<FixtureHost> StartAsync()
    {
        var host = new FixtureHost(new CallRecorder());
        host.Servers = FixtureServers.All(host.Recorder);

        foreach (var server in host.Servers)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new Implementation { Name = server.Name, Title = server.Title, Version = "1.0.0" };
                    o.ServerInstructions = server.Instructions;
                })
                .WithHttpTransport()
                .WithTools(server.Tools);

            var app = builder.Build();
            app.MapMcp("/mcp");
            await app.StartAsync();
            host._apps.Add(app);

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            host.Urls[server.Name] = $"{address}/mcp";
        }

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
