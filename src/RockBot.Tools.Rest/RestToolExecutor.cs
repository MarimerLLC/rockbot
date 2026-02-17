using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RockBot.Tools.Rest;

/// <summary>
/// Executes a tool invocation by calling a REST endpoint.
/// </summary>
internal sealed class RestToolExecutor(
    RestEndpointDefinition endpoint,
    IHttpClientFactory httpClientFactory) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("RockBot.Tools.Rest");

        var arguments = ParseArguments(request.Arguments);
        var url = ExpandUrlTemplate(endpoint.UrlTemplate, arguments);

        using var httpRequest = new HttpRequestMessage(new HttpMethod(endpoint.Method), url);

        ApplyAuth(httpRequest);

        if (endpoint.SendBodyAsJson && request.Arguments is not null
            && endpoint.Method is "POST" or "PUT" or "PATCH")
        {
            httpRequest.Content = new StringContent(request.Arguments, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(httpRequest, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = content,
                IsError = true
            };
        }

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = false
        };
    }

    internal static string ExpandUrlTemplate(string template, Dictionary<string, string> arguments)
    {
        var result = template;
        foreach (var (key, value) in arguments)
        {
            result = result.Replace($"{{{key}}}", Uri.EscapeDataString(value));
        }
        return result;
    }

    private static Dictionary<string, string> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        if (dict is null)
            return [];

        return dict.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (endpoint.AuthType == "none" || endpoint.AuthEnvVar is null)
            return;

        var token = Environment.GetEnvironmentVariable(endpoint.AuthEnvVar);
        if (string.IsNullOrEmpty(token))
            return;

        switch (endpoint.AuthType)
        {
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            case "api_key":
                request.Headers.Add(endpoint.ApiKeyHeader, token);
                break;
        }
    }
}
