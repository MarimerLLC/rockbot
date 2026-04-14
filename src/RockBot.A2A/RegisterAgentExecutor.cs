using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Registers or updates an HTTP-based A2A agent in the directory.
/// Supports optional auth header configuration for agents that require API keys.
/// Auto-detects the A2A protocol version by probing the remote agent's well-known
/// card endpoint (v1: <c>/.well-known/a2a/agent-card</c>, v0.3: <c>/.well-known/agent-card.json</c>).
/// </summary>
internal sealed class RegisterAgentExecutor(
    IAgentDirectory directory,
    IHttpClientFactory httpClientFactory,
    ILogger<RegisterAgentExecutor> logger) : IToolExecutor
{
    private sealed record DetectedProtocol(string? Version, bool SupportsStreaming);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments, JsonOptions) ?? [];
        }
        catch
        {
            return Error(request, "Invalid arguments JSON.");
        }

        if (!TryGetString(args, "agent_name", out var agentName))
            return Error(request, "Missing required parameter: agent_name");
        if (!TryGetString(args, "url", out var url))
            return Error(request, "Missing required parameter: url");

        TryGetString(args, "description", out var description);
        TryGetString(args, "auth_header_name", out var authHeaderName);
        TryGetString(args, "auth_header_value_base64", out var authHeaderValueBase64);
        TryGetString(args, "protocol_version", out var explicitProtocolVersion);

        // Parse optional skills array
        List<AgentSkill>? skills = null;
        if (args.TryGetValue("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Array)
        {
            try
            {
                skills = JsonSerializer.Deserialize<List<AgentSkill>>(skillsEl.GetRawText(), JsonOptions);
            }
            catch
            {
                return Error(request, "Invalid skills array format. Expected: [{\"id\": \"...\", \"name\": \"...\", \"description\": \"...\"}]");
            }
        }

        // Validate auth: both header name and value must be provided together
        if (!string.IsNullOrEmpty(authHeaderName) != !string.IsNullOrEmpty(authHeaderValueBase64))
            return Error(request, "auth_header_name and auth_header_value_base64 must be provided together.");

        // Validate base64 encoding
        if (!string.IsNullOrEmpty(authHeaderValueBase64))
        {
            try
            {
                Convert.FromBase64String(authHeaderValueBase64);
            }
            catch (FormatException)
            {
                return Error(request, "auth_header_value_base64 is not valid base64.");
            }
        }

        // Auto-detect protocol version from the remote agent's well-known card
        // unless the caller explicitly specified one.
        DetectedProtocol? detected = null;
        if (string.IsNullOrEmpty(explicitProtocolVersion))
        {
            detected = await DetectProtocolVersionAsync(url, authHeaderName, authHeaderValueBase64, ct);
        }

        var protocolVersion = !string.IsNullOrEmpty(explicitProtocolVersion)
            ? explicitProtocolVersion
            : detected?.Version;

        // When updating an existing agent, preserve fields that weren't provided in this call
        // (e.g. auth config, description, skills) so a simple URL update doesn't wipe them.
        var existing = directory.GetAgent(agentName);

        var card = new AgentCard
        {
            AgentName = agentName,
            Url = url,
            Description = string.IsNullOrEmpty(description) ? existing?.Description : description,
            Skills = skills ?? existing?.Skills,
            AuthHeaderName = string.IsNullOrEmpty(authHeaderName) ? existing?.AuthHeaderName : authHeaderName,
            AuthHeaderValueBase64 = string.IsNullOrEmpty(authHeaderValueBase64) ? existing?.AuthHeaderValueBase64 : authHeaderValueBase64,
            ProtocolVersion = protocolVersion ?? existing?.ProtocolVersion,
            SupportsStreaming = detected?.SupportsStreaming ?? existing?.SupportsStreaming
        };

        directory.AddOrUpdate(card);

        var authNote = !string.IsNullOrEmpty(authHeaderName) ? $" Auth header '{authHeaderName}' configured." : "";
        var skillNote = skills is { Count: > 0 } ? $" {skills.Count} skill(s) registered." : "";
        var versionNote = !string.IsNullOrEmpty(protocolVersion) ? $" Protocol version: {protocolVersion}." : "";
        logger.LogInformation("Registered agent '{AgentName}' at {Url}{Auth}{Version}",
            agentName, url, authNote, versionNote);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = $"Agent '{agentName}' registered at {url}.{skillNote}{authNote}{versionNote}",
            IsError = false
        };
    }

    /// <summary>
    /// Probes the remote agent's well-known card endpoints to detect the A2A protocol version
    /// and streaming capability. Tries v1 (<c>/.well-known/a2a/agent-card</c>) first,
    /// then v0.3 (<c>/.well-known/agent-card.json</c>).
    /// Returns null if detection fails (caller falls back to existing behavior).
    /// </summary>
    private async Task<DetectedProtocol?> DetectProtocolVersionAsync(
        string baseUrl,
        string? authHeaderName,
        string? authHeaderValueBase64,
        CancellationToken ct)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            if (!string.IsNullOrEmpty(authHeaderName) &&
                !string.IsNullOrEmpty(authHeaderValueBase64))
            {
                var headerValue = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(authHeaderValueBase64));
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(authHeaderName, headerValue);
            }

            var trimmedUrl = baseUrl.TrimEnd('/');

            // Try v1 well-known endpoint first
            var v1Result = await TryDetectV1Async(httpClient, trimmedUrl, ct);
            if (v1Result is not null)
                return v1Result;

            // Try v0.3 well-known endpoint
            var v03Result = await TryDetectV03Async(httpClient, trimmedUrl, ct);
            if (v03Result is not null)
                return v03Result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Protocol version auto-detection failed for {Url}", baseUrl);
        }

        return null;
    }

    private async Task<DetectedProtocol?> TryDetectV1Async(HttpClient httpClient, string trimmedUrl, CancellationToken ct)
    {
        try
        {
            var response = await httpClient.GetAsync($"{trimmedUrl}/.well-known/a2a/agent-card", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // v1 cards have "supportedInterfaces" array
            if (root.TryGetProperty("supportedInterfaces", out var interfaces) &&
                interfaces.ValueKind == JsonValueKind.Array)
            {
                // Check capabilities.streaming
                bool streaming = root.TryGetProperty("capabilities", out var caps) &&
                    caps.TryGetProperty("streaming", out var streamProp) &&
                    streamProp.ValueKind == JsonValueKind.True;

                // Extract the protocol version from the first interface if available
                foreach (var iface in interfaces.EnumerateArray())
                {
                    if (iface.TryGetProperty("protocolVersion", out var pv) &&
                        pv.ValueKind == JsonValueKind.String)
                    {
                        var version = pv.GetString();
                        if (!string.IsNullOrEmpty(version))
                        {
                            logger.LogInformation(
                                "Detected A2A protocol version '{Version}' from v1 agent card at {Url} (streaming={Streaming})",
                                version, trimmedUrl, streaming);
                            return new DetectedProtocol(version, streaming);
                        }
                    }
                }

                // Has supportedInterfaces but no explicit protocolVersion — assume 1.0
                logger.LogInformation(
                    "Detected A2A v1 agent card (supportedInterfaces present) at {Url} (streaming={Streaming})",
                    trimmedUrl, streaming);
                return new DetectedProtocol("1.0", streaming);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "v1 well-known probe failed for {Url}", trimmedUrl);
        }

        return null;
    }

    private async Task<DetectedProtocol?> TryDetectV03Async(HttpClient httpClient, string trimmedUrl, CancellationToken ct)
    {
        try
        {
            var response = await httpClient.GetAsync($"{trimmedUrl}/.well-known/agent-card.json", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // v0.3 cards may have a top-level "protocolVersion" field
            if (root.TryGetProperty("protocolVersion", out var pv) &&
                pv.ValueKind == JsonValueKind.String)
            {
                var version = pv.GetString();
                if (!string.IsNullOrEmpty(version))
                {
                    logger.LogInformation(
                        "Detected A2A protocol version '{Version}' from v0.3 agent card at {Url}",
                        version, trimmedUrl);
                    return new DetectedProtocol(version, SupportsStreaming: false);
                }
            }

            // Card exists at v0.3 endpoint but no explicit version — assume 0.3
            logger.LogInformation("Detected A2A v0.3 agent card at {Url}", trimmedUrl);
            return new DetectedProtocol("0.3", SupportsStreaming: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "v0.3 well-known probe failed for {Url}", trimmedUrl);
        }

        return null;
    }

    private static bool TryGetString(Dictionary<string, JsonElement> args, string key, out string value)
    {
        if (args.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(value);
        }
        value = string.Empty;
        return false;
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
