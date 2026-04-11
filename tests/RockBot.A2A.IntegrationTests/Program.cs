using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.A2A.IntegrationTests;
using RockBot.Messaging;
using RockBot.Messaging.RabbitMQ;
using Spectre.Console;

AnsiConsole.MarkupLine("[bold blue]RockBot A2A Integration Test Harness[/]");
AnsiConsole.WriteLine();

// ── Configuration from environment ───────────────────────────────────────
var config = new TestConfig
{
    RabbitMqHost = Environment.GetEnvironmentVariable("RabbitMq__HostName") ?? "localhost",
    RabbitMqPort = int.TryParse(Environment.GetEnvironmentVariable("RabbitMq__Port"), out var p) ? p : 5672,
    RabbitMqUser = Environment.GetEnvironmentVariable("RabbitMq__UserName") ?? "rockbot",
    RabbitMqPassword = Environment.GetEnvironmentVariable("RabbitMq__Password") ?? "rockbot",
    GatewayUrl = Environment.GetEnvironmentVariable("A2A_GATEWAY_URL") ?? "http://localhost:5200",
    TrustStorePath = Environment.GetEnvironmentVariable("TRUST_STORE_PATH") ?? "/data/agent/agent-trust.json"
};

AnsiConsole.MarkupLine($"  RabbitMQ: [cyan]{config.RabbitMqHost}:{config.RabbitMqPort}[/]");
AnsiConsole.MarkupLine($"  A2A Gateway: [cyan]{config.GatewayUrl}[/]");
AnsiConsole.MarkupLine($"  Trust store: [cyan]{config.TrustStorePath}[/]");
AnsiConsole.WriteLine();

// ── Build DI container ───────────────────────────────────────────────────
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddRockBotRabbitMq(opts =>
{
    opts.HostName = config.RabbitMqHost;
    opts.Port = config.RabbitMqPort;
    opts.UserName = config.RabbitMqUser;
    opts.Password = config.RabbitMqPassword;
    // Use the default "rockbot" exchange — same as the agents
});
services.AddHttpClient();

await using var provider = services.BuildServiceProvider();

// ── Wait for RabbitMQ ────────────────────────────────────────────────────
await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync("Connecting to RabbitMQ...", async ctx =>
    {
        var maxRetries = 30;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                var pub = provider.GetRequiredService<IMessagePublisher>();
                // A quick publish to a throwaway topic verifies connectivity
                var probe = new RockBot.Messaging.MessageEnvelope
                {
                    MessageId = "probe",
                    MessageType = "probe",
                    Body = Array.Empty<byte>(),
                    Source = "test-harness",
                    Timestamp = DateTimeOffset.UtcNow
                };
                await pub.PublishAsync("test.probe", probe);
                return;
            }
            catch
            {
                ctx.Status($"Connecting to RabbitMQ... (attempt {i + 1}/{maxRetries})");
                await Task.Delay(2000);
            }
        }
        throw new Exception($"Could not connect to RabbitMQ at {config.RabbitMqHost}:{config.RabbitMqPort}");
    });

AnsiConsole.MarkupLine("[green]Connected to RabbitMQ[/]");
AnsiConsole.WriteLine();

// ── Run test scenarios ───────────────────────────────────────────────────
var runner = new TestRunner(provider, config);
var results = await runner.RunAllAsync();

// ── Print results ────────────────────────────────────────────────────────
AnsiConsole.WriteLine();
var table = new Table()
    .Border(TableBorder.Rounded)
    .AddColumn("#")
    .AddColumn("Scenario")
    .AddColumn("Result")
    .AddColumn("Time");

for (var i = 0; i < results.Count; i++)
{
    var r = results[i];
    var status = r.Passed
        ? "[green]PASS[/]"
        : $"[red]FAIL[/]";
    var time = $"{r.Elapsed.TotalSeconds:F1}s";
    table.AddRow($"{i + 1}", Markup.Escape(r.Name), status, time);

    if (!r.Passed && r.Error is not null)
        table.AddRow("", $"[red]{Markup.Escape(r.Error)}[/]", "", "");
}

AnsiConsole.Write(table);

var passed = results.Count(r => r.Passed);
var failed = results.Count(r => !r.Passed);
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine($"[bold]{passed} passed, {failed} failed[/]");

return failed == 0 ? 0 : 1;
