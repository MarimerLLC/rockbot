using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using A2AV1 = A2A;

namespace RockBot.A2A.IntegrationTests.Scenarios;

/// <summary>
/// Tests the HTTP-based A2A v1 protocol against the A2A gateway (which bridges to RockBot).
/// </summary>
internal static class HttpA2AScenarios
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Scenario 1: Fetch RockBot's agent card from the gateway's well-known endpoint.
    /// </summary>
    public static async Task FetchAgentCardAsync(string gatewayUrl, IServiceProvider services, CancellationToken ct)
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();

        // Retry until the gateway is ready
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                response = await httpClient.GetAsync($"{gatewayUrl}/.well-known/agent-card.json", ct);
                if (response.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(1000, ct);
        }
        Assert(response is not null, "Could not connect to A2A gateway after retries");
        response!.EnsureSuccessStatusCode();

        // Use A2A v1 SDK's AgentCard type (Name, not AgentName)
        var card = await response.Content.ReadFromJsonAsync<A2AV1.AgentCard>(JsonOptions, ct);

        Assert(card is not null, "Agent card is null");
        Assert(card!.Name == "RockBot", $"Expected Name 'RockBot', got '{card.Name}'");
        Assert(card.Skills is { Count: >= 2 }, $"Expected at least 2 skills, got {card.Skills?.Count ?? 0}");

        var skillIds = card.Skills!.Select(s => s.Id).ToList();
        Assert(skillIds.Contains("notify-user"), "Missing 'notify-user' skill");
        Assert(skillIds.Contains("query-availability"), "Missing 'query-availability' skill");
    }

    /// <summary>
    /// Scenario 2: Send a task to RockBot via the A2A v1 SDK through the gateway.
    /// This exercises the full A2A protocol: SDK client → HTTP JSON-RPC → gateway → RabbitMQ → RockBot → response.
    /// </summary>
    public static async Task SendTaskViaA2ASdkAsync(string gatewayUrl, string? apiKey, IServiceProvider services, CancellationToken ct)
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        if (apiKey is not null)
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        // Ensure gateway is ready
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                var probe = await httpClient.GetAsync($"{gatewayUrl}/.well-known/agent-card.json", ct);
                if (probe.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(1000, ct);
        }

        var endpoint = new Uri(gatewayUrl.TrimEnd('/'));
        var a2aClient = new A2AV1.A2AClient(endpoint, httpClient);

        var taskId = Guid.NewGuid().ToString("N");
        var sendRequest = new A2AV1.SendMessageRequest
        {
            Message = new A2AV1.Message
            {
                Role = A2AV1.Role.User,
                MessageId = taskId,
                Parts = [new A2AV1.Part { Text = "Integration test: notify user about meeting change" }]
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["skill"] = JsonSerializer.SerializeToElement("notify-user")
            }
        };

        var response = await a2aClient.SendMessageAsync(sendRequest, ct);
        Assert(response is not null, "A2A v1 response is null");

        // The response should be a Message (immediate reply) since the gateway
        // waits for RockBot's result before responding.
        var hasContent = response.PayloadCase switch
        {
            A2AV1.SendMessageResponseCase.Message when response.Message is { } msg =>
                msg.Parts.Any(p => !string.IsNullOrEmpty(p.Text)),
            A2AV1.SendMessageResponseCase.Task when response.Task is { } task =>
                task.Status.Message?.Parts.Any(p => !string.IsNullOrEmpty(p.Text)) ?? false,
            _ => false
        };

        Assert(hasContent, $"Expected text content in A2A response, got PayloadCase={response.PayloadCase}");
    }

    /// <summary>
    /// Scenario 3: POST to the gateway without an API key should be rejected with a JSON-RPC error.
    /// </summary>
    public static async Task UnauthenticatedRequestRejectedAsync(string gatewayUrl, IServiceProvider services, CancellationToken ct)
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        // Intentionally no X-Api-Key header

        // Ensure gateway is ready
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                var probe = await httpClient.GetAsync($"{gatewayUrl}/.well-known/agent-card.json", ct);
                if (probe.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(1000, ct);
        }

        var jsonRpc = """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"role":"user","parts":[{"text":"test"}]}}}""";
        var content = new System.Net.Http.StringContent(jsonRpc, System.Text.Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync(gatewayUrl, content, ct);

        Assert(response.StatusCode == System.Net.HttpStatusCode.Unauthorized,
            $"Expected 401 Unauthorized, got {(int)response.StatusCode} {response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert(body.Contains("jsonrpc"), $"Expected JSON-RPC error body, got: {body}");
        Assert(body.Contains("Authentication required"), $"Expected auth error message, got: {body}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
