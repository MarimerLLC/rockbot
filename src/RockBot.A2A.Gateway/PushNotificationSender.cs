using System.Text.Json;
using A2A;

namespace RockBot.A2A.Gateway;

/// <summary>
/// Sends HTTP POST webhooks to push notification config URLs when task status changes.
/// Fire-and-forget with logging — webhook failures do not block the caller.
/// </summary>
internal sealed class PushNotificationSender(
    FilePushNotificationConfigStore configStore,
    IHttpClientFactory httpClientFactory,
    ILogger<PushNotificationSender> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan WebhookTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends the given <see cref="TaskStatusUpdateEvent"/> to all push notification configs
    /// registered for the task. Non-blocking — failures are logged but do not propagate.
    /// </summary>
    public async Task TrySendStatusUpdateAsync(string taskId, TaskStatusUpdateEvent statusUpdate, CancellationToken ct)
    {
        var configs = await configStore.GetConfigsForTaskAsync(taskId, ct);
        if (configs.Count == 0) return;

        var payload = JsonSerializer.Serialize(new StreamResponse { StatusUpdate = statusUpdate }, JsonOptions);
        await SendToAllAsync(configs, payload, taskId, ct);
    }

    /// <summary>
    /// Sends a completed task notification to all push notification configs.
    /// </summary>
    public async Task TrySendTaskCompletedAsync(string taskId, AgentTask task, CancellationToken ct)
    {
        var configs = await configStore.GetConfigsForTaskAsync(taskId, ct);
        if (configs.Count == 0) return;

        var payload = JsonSerializer.Serialize(new StreamResponse { Task = task }, JsonOptions);
        await SendToAllAsync(configs, payload, taskId, ct);
    }

    private async Task SendToAllAsync(
        List<TaskPushNotificationConfig> configs, string payload, string taskId, CancellationToken ct)
    {
        foreach (var config in configs)
        {
            _ = Task.Run(() => SendWebhookAsync(config, payload, taskId), ct);
        }
        await Task.CompletedTask;
    }

    private async Task SendWebhookAsync(TaskPushNotificationConfig config, string payload, string taskId)
    {
        var url = config.PushNotificationConfig?.Url;
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            using var cts = new CancellationTokenSource(WebhookTimeout);
            var httpClient = httpClientFactory.CreateClient();

            // Attach authentication if configured
            if (config.PushNotificationConfig.Authentication is { } auth &&
                !string.IsNullOrEmpty(auth.Scheme) && !string.IsNullOrEmpty(auth.Credentials))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(auth.Scheme, auth.Credentials);
            }

            // Attach token as bearer if configured (shorthand)
            if (!string.IsNullOrEmpty(config.PushNotificationConfig.Token))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.PushNotificationConfig.Token);
            }

            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content, cts.Token);

            logger.LogDebug(
                "Push notification for task {TaskId} to {Url}: {StatusCode}",
                taskId, url, response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            logger.LogWarning(
                "Push notification failed for task {TaskId} to {Url}: {Error}",
                taskId, url, ex.Message);
        }
    }
}
