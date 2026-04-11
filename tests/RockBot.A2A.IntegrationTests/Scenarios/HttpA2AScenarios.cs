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

    /// <summary>
    /// Scenario 4: Verify the agent card advertises streaming, push notification, and extended card capabilities.
    /// </summary>
    public static async Task AgentCardCapabilitiesAsync(string gatewayUrl, IServiceProvider services, CancellationToken ct)
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();

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

        var card = await response.Content.ReadFromJsonAsync<A2AV1.AgentCard>(JsonOptions, ct);
        Assert(card is not null, "Agent card is null");
        Assert(card!.Capabilities is not null, "Agent card Capabilities is null");
        Assert(card.Capabilities!.Streaming == true, $"Expected Streaming=true, got {card.Capabilities.Streaming}");
        Assert(card.Capabilities.PushNotifications == true, $"Expected PushNotifications=true, got {card.Capabilities.PushNotifications}");
        Assert(card.Capabilities.ExtendedAgentCard == true, $"Expected ExtendedAgentCard=true, got {card.Capabilities.ExtendedAgentCard}");
    }

    /// <summary>
    /// Scenario 5: Send a task, then verify it appears in ListTasks.
    /// </summary>
    public static async Task SendAndListTasksAsync(string gatewayUrl, string? apiKey, IServiceProvider services, CancellationToken ct)
    {
        var a2aClient = CreateA2AClient(gatewayUrl, apiKey, services);
        await WaitForGateway(gatewayUrl, services, ct);

        // Send a task first
        var sendRequest = new A2AV1.SendMessageRequest
        {
            Message = new A2AV1.Message
            {
                Role = A2AV1.Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [new A2AV1.Part { Text = "Integration test: ListTasks verification" }]
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["skill"] = JsonSerializer.SerializeToElement("notify-user")
            }
        };
        var sendResponse = await a2aClient.SendMessageAsync(sendRequest, ct);
        Assert(sendResponse is not null, "SendMessage response is null");

        // Now list tasks — should include the one we just sent
        var listResponse = await a2aClient.ListTasksAsync(new A2AV1.ListTasksRequest(), ct);
        Assert(listResponse is not null, "ListTasks response is null");
        Assert(listResponse!.Tasks is not null, "ListTasks Tasks list is null");
        Assert(listResponse.Tasks.Count >= 1, $"Expected at least 1 task, got {listResponse.Tasks.Count}");
    }

    /// <summary>
    /// Scenario 6: Send a streaming message and consume SSE events.
    /// The EchoChatClient agent will process the task and return a result;
    /// we verify that at least one StreamResponse event arrives via SSE.
    /// </summary>
    public static async Task SendStreamingMessageAsync(string gatewayUrl, string? apiKey, IServiceProvider services, CancellationToken ct)
    {
        var a2aClient = CreateA2AClient(gatewayUrl, apiKey, services);
        await WaitForGateway(gatewayUrl, services, ct);

        var sendRequest = new A2AV1.SendMessageRequest
        {
            Message = new A2AV1.Message
            {
                Role = A2AV1.Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [new A2AV1.Part { Text = "Integration test: streaming response" }]
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["skill"] = JsonSerializer.SerializeToElement("notify-user")
            }
        };

        var events = new List<A2AV1.StreamResponse>();
        await foreach (var evt in a2aClient.SendStreamingMessageAsync(sendRequest, ct))
        {
            events.Add(evt);
        }

        Assert(events.Count >= 1, $"Expected at least 1 SSE event, got {events.Count}");

        // The last event should contain either a message or a completed task
        var last = events[^1];
        var hasContent = last.PayloadCase switch
        {
            A2AV1.StreamResponseCase.Message when last.Message is { } msg =>
                msg.Parts.Any(p => !string.IsNullOrEmpty(p.Text)),
            A2AV1.StreamResponseCase.Task when last.Task is { } task =>
                task.Status?.State is A2AV1.TaskState.Completed or A2AV1.TaskState.Working,
            A2AV1.StreamResponseCase.StatusUpdate when last.StatusUpdate is { } su =>
                su.Status is not null,
            _ => false
        };
        Assert(hasContent, $"Expected content in final SSE event, got PayloadCase={last.PayloadCase}");
    }

    /// <summary>
    /// Scenario 7: Exercise push notification config CRUD — create, get, list, delete.
    /// If the SDK's A2AServer doesn't support push notifications (no store wired up),
    /// the test catches the expected error and passes with a note.
    /// </summary>
    public static async Task PushNotificationConfigCrudAsync(string gatewayUrl, string? apiKey, IServiceProvider services, CancellationToken ct)
    {
        var a2aClient = CreateA2AClient(gatewayUrl, apiKey, services);
        await WaitForGateway(gatewayUrl, services, ct);

        // First, create a task so we have a valid task ID
        var sendRequest = new A2AV1.SendMessageRequest
        {
            Message = new A2AV1.Message
            {
                Role = A2AV1.Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [new A2AV1.Part { Text = "Integration test: push notification CRUD" }]
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["skill"] = JsonSerializer.SerializeToElement("notify-user")
            }
        };
        var sendResponse = await a2aClient.SendMessageAsync(sendRequest, ct);
        Assert(sendResponse is not null, "SendMessage response is null");

        // Extract task ID from the response
        var taskId = sendResponse!.PayloadCase == A2AV1.SendMessageResponseCase.Task
            ? sendResponse.Task?.Id
            : null;

        // If we got a Message (not a Task), get the task ID from ListTasks
        if (taskId is null)
        {
            var list = await a2aClient.ListTasksAsync(new A2AV1.ListTasksRequest(), ct);
            taskId = list?.Tasks?.FirstOrDefault()?.Id;
        }
        Assert(taskId is not null, "Could not obtain a task ID for push notification CRUD");

        var configId = Guid.NewGuid().ToString("N");

        // Create
        var created = await a2aClient.CreateTaskPushNotificationConfigAsync(
            new A2AV1.CreateTaskPushNotificationConfigRequest
            {
                TaskId = taskId!,
                ConfigId = configId,
                Config = new A2AV1.PushNotificationConfig
                {
                    Url = "https://example.com/webhook",
                    Token = "test-token-123"
                }
            }, ct);
        Assert(created is not null, "CreateTaskPushNotificationConfig returned null");
        Assert(created!.Id == configId, $"Expected config ID '{configId}', got '{created.Id}'");

        // Get
        var fetched = await a2aClient.GetTaskPushNotificationConfigAsync(
            new A2AV1.GetTaskPushNotificationConfigRequest
            {
                TaskId = taskId!,
                Id = configId
            }, ct);
        Assert(fetched is not null, "GetTaskPushNotificationConfig returned null");
        Assert(fetched!.PushNotificationConfig?.Url == "https://example.com/webhook",
            $"Expected URL 'https://example.com/webhook', got '{fetched.PushNotificationConfig?.Url}'");

        // List
        var listed = await a2aClient.ListTaskPushNotificationConfigAsync(
            new A2AV1.ListTaskPushNotificationConfigRequest { TaskId = taskId! }, ct);
        Assert(listed is not null, "ListTaskPushNotificationConfig returned null");
        Assert(listed!.Configs.Any(c => c.Id == configId),
            "Created config not found in list");

        // Delete
        await a2aClient.DeleteTaskPushNotificationConfigAsync(
            new A2AV1.DeleteTaskPushNotificationConfigRequest
            {
                TaskId = taskId!,
                Id = configId
            }, ct);

        // Verify deletion
        var afterDelete = await a2aClient.ListTaskPushNotificationConfigAsync(
            new A2AV1.ListTaskPushNotificationConfigRequest { TaskId = taskId! }, ct);
        Assert(!afterDelete!.Configs.Any(c => c.Id == configId),
            "Config still present after deletion");
    }

    /// <summary>
    /// Scenario 8: Fetch the extended agent card with capabilities.
    /// </summary>
    public static async Task GetExtendedAgentCardAsync(string gatewayUrl, string? apiKey, IServiceProvider services, CancellationToken ct)
    {
        var a2aClient = CreateA2AClient(gatewayUrl, apiKey, services);
        await WaitForGateway(gatewayUrl, services, ct);

        var card = await a2aClient.GetExtendedAgentCardAsync(
            new A2AV1.GetExtendedAgentCardRequest(), ct);
        Assert(card is not null, "Extended agent card is null");
        Assert(card!.Name == "RockBot", $"Expected Name 'RockBot', got '{card.Name}'");
        Assert(card.Capabilities is not null, "Extended card should include capabilities");
        Assert(card.Capabilities!.Streaming == true, "Extended card should advertise streaming");
        Assert(card.Skills is { Count: >= 2 }, $"Expected at least 2 skills, got {card.Skills?.Count ?? 0}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static A2AV1.A2AClient CreateA2AClient(string gatewayUrl, string? apiKey, IServiceProvider services)
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        if (apiKey is not null)
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        return new A2AV1.A2AClient(new Uri(gatewayUrl.TrimEnd('/')), httpClient);
    }

    private static async Task WaitForGateway(string gatewayUrl, IServiceProvider services, CancellationToken ct)
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();

        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                var probe = await httpClient.GetAsync($"{gatewayUrl}/.well-known/agent-card.json", ct);
                if (probe.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(1000, ct);
        }
        throw new Exception("Gateway not ready after 15 retries");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
