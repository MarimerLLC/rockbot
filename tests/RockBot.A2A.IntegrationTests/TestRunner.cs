using System.Diagnostics;
using Spectre.Console;

namespace RockBot.A2A.IntegrationTests;

internal sealed class TestConfig
{
    public required string RabbitMqHost { get; init; }
    public required int RabbitMqPort { get; init; }
    public required string RabbitMqUser { get; init; }
    public required string RabbitMqPassword { get; init; }
    public required string GatewayUrl { get; init; }
    public required string TrustStorePath { get; init; }
    public string? GatewayApiKey { get; init; }
}

internal sealed record TestResult(string Name, bool Passed, TimeSpan Elapsed, string? Error = null);

internal sealed class TestRunner(IServiceProvider services, TestConfig config)
{
    public async Task<List<TestResult>> RunAllAsync()
    {
        var results = new List<TestResult>();

        // HTTP A2A v1 scenarios (against gateway → RockBot)
        results.Add(await RunAsync("A2A: Agent Card Discovery",
            ct => Scenarios.HttpA2AScenarios.FetchAgentCardAsync(config.GatewayUrl, services, ct),
            timeout: TimeSpan.FromSeconds(30)));

        results.Add(await RunAsync("A2A: Send Task via v1 SDK",
            ct => Scenarios.HttpA2AScenarios.SendTaskViaA2ASdkAsync(config.GatewayUrl, config.GatewayApiKey, services, ct),
            timeout: TimeSpan.FromSeconds(90)));

        // Gateway auth — unauthenticated requests should be rejected
        results.Add(await RunAsync("A2A: Unauthenticated Request Rejected",
            ct => Scenarios.HttpA2AScenarios.UnauthenticatedRequestRejectedAsync(config.GatewayUrl, services, ct),
            timeout: TimeSpan.FromSeconds(15)));

        // Agent card capabilities (streaming, push notifications, extended card)
        results.Add(await RunAsync("A2A: Agent Card Capabilities",
            ct => Scenarios.HttpA2AScenarios.AgentCardCapabilitiesAsync(config.GatewayUrl, services, ct),
            timeout: TimeSpan.FromSeconds(30)));

        // ListTasks — send a task, then verify it appears in the list
        results.Add(await RunAsync("A2A: Send + ListTasks",
            ct => Scenarios.HttpA2AScenarios.SendAndListTasksAsync(config.GatewayUrl, config.GatewayApiKey, services, ct),
            timeout: TimeSpan.FromSeconds(90)));

        // SSE streaming — send a streaming request and consume events
        results.Add(await RunAsync("A2A: SendStreamingMessage",
            ct => Scenarios.HttpA2AScenarios.SendStreamingMessageAsync(config.GatewayUrl, config.GatewayApiKey, services, ct),
            timeout: TimeSpan.FromSeconds(90)));

        // Outbound streaming consumption — exercises the DispatchV1StreamingAsync event processing path
        results.Add(await RunAsync("A2A: Outbound Streaming Consumption",
            ct => Scenarios.HttpA2AScenarios.OutboundStreamingConsumptionAsync(config.GatewayUrl, config.GatewayApiKey, services, ct),
            timeout: TimeSpan.FromSeconds(90)));

        // Push notification config CRUD
        results.Add(await RunAsync("A2A: Push Notification Config CRUD",
            ct => Scenarios.HttpA2AScenarios.PushNotificationConfigCrudAsync(config.GatewayUrl, config.GatewayApiKey, services, ct),
            timeout: TimeSpan.FromSeconds(60)));

        // Extended agent card
        results.Add(await RunAsync("A2A: GetExtendedAgentCard",
            ct => Scenarios.HttpA2AScenarios.GetExtendedAgentCardAsync(config.GatewayUrl, config.GatewayApiKey, services, ct),
            timeout: TimeSpan.FromSeconds(30)));

        // RabbitMQ discovery — RockBot may take a while to start
        results.Add(await RunAsync("Discovery: RockBot AgentCard",
            ct => Scenarios.DiscoveryScenario.VerifyAnnouncementAsync(services, ct),
            timeout: TimeSpan.FromSeconds(120)));

        // Inbound tasks to RockBot
        results.Add(await RunAsync("Inbound: notify-user Task",
            ct => Scenarios.InboundTaskScenarios.SendNotifyUserAsync(services, ct),
            timeout: TimeSpan.FromSeconds(90)));

        // Trust store verification (depends on inbound task above)
        results.Add(await RunAsync("Trust Store: Entry Created",
            ct => Scenarios.TrustStoreScenario.VerifyTrustEntryAsync(config.TrustStorePath, ct)));

        // Identity verification — empty source should be rejected
        results.Add(await RunAsync("Identity: Empty Source Rejected",
            ct => Scenarios.InboundTaskScenarios.EmptySourceRejectedAsync(services, ct),
            timeout: TimeSpan.FromSeconds(15)));

        return results;
    }

    private static async Task<TestResult> RunAsync(
        string name,
        Func<CancellationToken, Task> scenario,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(15);

        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Running: {name}", async _ =>
            {
                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(timeout.Value);
                try
                {
                    await scenario(cts.Token);
                    sw.Stop();
                    AnsiConsole.MarkupLine($"  [green]PASS[/] {Markup.Escape(name)} ({sw.Elapsed.TotalSeconds:F1}s)");
                    return new TestResult(name, true, sw.Elapsed);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    sw.Stop();
                    var err = $"Timed out after {timeout.Value.TotalSeconds:F0}s";
                    AnsiConsole.MarkupLine($"  [red]FAIL[/] {Markup.Escape(name)} — {err}");
                    return new TestResult(name, false, sw.Elapsed, err);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    var err = ex.Message;
                    AnsiConsole.MarkupLine($"  [red]FAIL[/] {Markup.Escape(name)} — {Markup.Escape(err)}");
                    return new TestResult(name, false, sw.Elapsed, err);
                }
            });
    }
}
