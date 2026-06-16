using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using A2AV1 = A2A;

namespace RockBot.A2A.IntegrationTests.Scenarios;

/// <summary>
/// End-to-end JWT/Bearer authentication and claims-propagation scenarios. These run only
/// when an OIDC provider is configured (see <see cref="TestConfig.JwtEnabled"/>). They mint
/// real tokens from the mock issuer, exercise the gateway's Bearer auth, and verify that the
/// agent recorded an independently-verified (non-self-asserted) identity.
/// </summary>
internal static class JwtAuthScenarios
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// The published agent card advertises a bearer/JWT scheme (plus the OIDC discovery
    /// scheme) in addition to apiKey, as a separate OR-ed security requirement.
    /// </summary>
    public static async Task AgentCardAdvertisesBearerAsync(string gatewayUrl, IServiceProvider services, CancellationToken ct)
    {
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        await WaitForGateway(http, gatewayUrl, ct);

        var json = await http.GetStringAsync($"{gatewayUrl}/.well-known/agent-card.json", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert(root.TryGetProperty("securitySchemes", out var schemes),
            "Agent card has no 'securitySchemes'");
        Assert(schemes.TryGetProperty("apiKey", out _), "Card missing 'apiKey' scheme");
        Assert(schemes.TryGetProperty("bearer", out var bearer),
            "Card missing 'bearer' scheme — JWT advertisement not present");
        // The bearer scheme should describe an HTTP bearer/JWT auth.
        Assert(bearer.TryGetProperty("httpAuthSecurityScheme", out var httpAuth)
                && string.Equals(httpAuth.GetProperty("scheme").GetString(), "bearer", StringComparison.OrdinalIgnoreCase),
            "Card 'bearer' scheme is not an http bearer scheme");
        Assert(schemes.TryGetProperty("openId", out _),
            "Card missing 'openId' OIDC discovery scheme");

        // apiKey and bearer must be in SEPARATE requirement entries (OR), not combined (AND).
        Assert(root.TryGetProperty("securityRequirements", out var reqs)
                && reqs.ValueKind == JsonValueKind.Array,
            "Agent card has no 'securityRequirements' array");
        var hasApiKeyReq = false;
        var hasBearerReq = false;
        foreach (var req in reqs.EnumerateArray())
        {
            if (!req.TryGetProperty("schemes", out var reqSchemes)) continue;
            var keys = reqSchemes.EnumerateObject().Select(p => p.Name).ToList();
            Assert(keys.Count == 1, $"A security requirement combined multiple schemes (AND): [{string.Join(", ", keys)}]");
            if (keys.Contains("apiKey")) hasApiKeyReq = true;
            if (keys.Contains("bearer")) hasBearerReq = true;
        }
        Assert(hasApiKeyReq, "No standalone apiKey security requirement");
        Assert(hasBearerReq, "No standalone bearer security requirement");
    }

    /// <summary>
    /// A request bearing a valid JWT from the mock issuer is accepted and bridged to RockBot.
    /// </summary>
    public static async Task BearerTokenAcceptedAsync(TestConfig config, IServiceProvider services, CancellationToken ct)
    {
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        await WaitForGateway(http, config.GatewayUrl, ct);

        var token = await GetAccessTokenAsync(http, config, ct);
        Assert(!string.IsNullOrWhiteSpace(token), "Did not obtain an access token from the OIDC provider");

        var bearerClient = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var a2aClient = new A2AV1.A2AClient(new Uri(config.GatewayUrl.TrimEnd('/')), bearerClient);

        var response = await a2aClient.SendMessageAsync(new A2AV1.SendMessageRequest
        {
            Message = new A2AV1.Message
            {
                Role = A2AV1.Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [new A2AV1.Part { Text = "Integration test: bearer-authenticated send" }]
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["skill"] = JsonSerializer.SerializeToElement("notify-user")
            }
        }, ct);

        Assert(response is not null, "Bearer-authenticated A2A response is null");
        var hasContent = response!.PayloadCase switch
        {
            A2AV1.SendMessageResponseCase.Message when response.Message is { } msg =>
                msg.Parts.Any(p => !string.IsNullOrEmpty(p.Text)),
            A2AV1.SendMessageResponseCase.Task when response.Task is { } task =>
                task.Status?.Message?.Parts.Any(p => !string.IsNullOrEmpty(p.Text)) ?? false,
            _ => false
        };
        Assert(hasContent, $"Expected text content in bearer A2A response, got PayloadCase={response.PayloadCase}");
    }

    /// <summary>
    /// A request with a malformed/garbage bearer token is rejected with 401.
    /// </summary>
    public static async Task InvalidTokenRejectedAsync(string gatewayUrl, IServiceProvider services, CancellationToken ct)
    {
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        await WaitForGateway(http, gatewayUrl, ct);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");
        var jsonRpc = """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"role":"user","parts":[{"text":"test"}]}}}""";
        var content = new StringContent(jsonRpc, System.Text.Encoding.UTF8, "application/json");
        var response = await http.PostAsync(gatewayUrl, content, ct);

        Assert(response.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected 401 for invalid bearer token, got {(int)response.StatusCode} {response.StatusCode}");
    }

    /// <summary>
    /// After a bearer-authenticated send, the agent's trust store records the caller under the
    /// JWT subject with an independently-verified (non-self-asserted) identity and the IdP issuer.
    /// This proves the gateway forwarded the verified claims and the agent honored them.
    /// </summary>
    public static async Task ClaimsPropagatedToAgentAsync(TestConfig config, IServiceProvider services, CancellationToken ct)
    {
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        await WaitForGateway(http, config.GatewayUrl, ct);

        // Send a bearer-authenticated task so the agent records a trust entry under the JWT sub.
        var token = await GetAccessTokenAsync(http, config, ct);
        var bearerClient = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var a2aClient = new A2AV1.A2AClient(new Uri(config.GatewayUrl.TrimEnd('/')), bearerClient);
        await a2aClient.SendMessageAsync(new A2AV1.SendMessageRequest
        {
            Message = new A2AV1.Message
            {
                Role = A2AV1.Role.User,
                MessageId = Guid.NewGuid().ToString("N"),
                Parts = [new A2AV1.Part { Text = "Integration test: claims propagation" }]
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["skill"] = JsonSerializer.SerializeToElement("notify-user")
            }
        }, ct);

        var expectedSubject = config.OidcExpectedSubject ?? "a2a-caller-007";
        var expectedIssuer = (config.OidcExpectedIssuer ?? "").TrimEnd('/');

        // Poll the trust store for the verified entry (the agent writes it after processing).
        AgentTrustEntry? entry = null;
        for (var attempt = 0; attempt < 20 && entry is null; attempt++)
        {
            if (File.Exists(config.TrustStorePath))
            {
                var json = await File.ReadAllTextAsync(config.TrustStorePath, ct);
                var entries = JsonSerializer.Deserialize<List<AgentTrustEntry>>(json, JsonOptions) ?? [];
                entry = entries.FirstOrDefault(e =>
                    string.Equals(e.AgentId, expectedSubject, StringComparison.Ordinal));
            }
            if (entry is null) await Task.Delay(1000, ct);
        }

        Assert(entry is not null,
            $"No trust entry for JWT subject '{expectedSubject}' — claims did not propagate to the agent");
        Assert(!entry!.IsSelfAsserted,
            "Trust entry is marked self-asserted — the agent did not treat the forwarded JWT claims as verified");
        var actualIssuer = (entry.Issuer ?? "").TrimEnd('/');
        Assert(string.Equals(actualIssuer, expectedIssuer, StringComparison.OrdinalIgnoreCase),
            $"Expected verified issuer '{expectedIssuer}', got '{entry.Issuer}'");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<string> GetAccessTokenAsync(HttpClient http, TestConfig config, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = config.OidcClientId ?? "",
            ["client_secret"] = config.OidcClientSecret ?? "",
            ["username"] = config.OidcUsername ?? "",
            ["password"] = config.OidcPassword ?? "",
            ["scope"] = config.OidcScope ?? "openid"
        };

        // The OIDC server may still be warming up; retry the token request briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var resp = await http.PostAsync(config.OidcTokenEndpoint, new FormUrlEncodedContent(form), ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"Token endpoint returned {(int)resp.StatusCode}: {body}");
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new Exception("Token response had no access_token");
            }
            catch (Exception) when (attempt < 15 && !ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
            }
        }
    }

    private static async Task WaitForGateway(HttpClient http, string gatewayUrl, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                var probe = await http.GetAsync($"{gatewayUrl}/.well-known/agent-card.json", ct);
                if (probe.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(1000, ct);
        }
        throw new Exception("Gateway not ready after 30 retries");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
