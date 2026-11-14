using Microsoft.Extensions.Configuration;

namespace RockBot.AgUi.DevHost;

/// <summary>
/// Runs the AG-UI proxy for local development of a client: no RabbitMQ, no LLM, a scripted agent.
/// <code>
/// dotnet run --project samples/RockBot.AgUi.DevHost -- --port 5140 --origin http://localhost:5180
/// </code>
/// </summary>
public static class DevHostProgram
{
    public static async Task Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables("ROCKBOT_DEVHOST_")
            .AddCommandLine(args)
            .Build();

        var port = configuration.GetValue("port", 5140);
        // The origin the WebAssembly sample is served from; without it the browser may not call the proxy.
        var origins = (configuration["origin"] ?? "http://localhost:5180")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await using var proxy = await DevProxy.StartAsync(new DevProxyOptions { Port = port, AllowedOrigins = origins });

        Console.WriteLine($"AG-UI proxy with a demo agent: http://localhost:{port}");
        Console.WriteLine($"Allowed browser origins: {string.Join(", ", origins)}");
        Console.WriteLine($"Attachments: {proxy.AttachmentsPath}");
        Console.WriteLine("Words the demo agent reacts to: question, notify, image, slow, silent. Ctrl+C stops.");

        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
        await stop.Task;
    }
}
