using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace McpServer.Slack.Services;

/// <summary>
/// Slack Web API transport. Every call is a form POST with the named workspace's user token; the
/// workspace comes from configuration, never from a token in a tool argument.
/// </summary>
public sealed class SlackApi(HttpClient http, IOptions<SlackOptions> options)
{
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(30);

    public IReadOnlyList<SlackWorkspaceOptions> Workspaces => options.Value.Workspaces;

    public SlackWorkspaceOptions Workspace(string name) =>
        options.Value.Workspaces.FirstOrDefault(w => w.Name == name)
        ?? throw new McpException(
            $"unknown workspace '{name}'; configured workspaces: {string.Join(", ", options.Value.Workspaces.Select(w => w.Name))}");

    /// <summary>Calls a Web API method and returns its JSON, or throws with a hint when Slack says ok:false.</summary>
    public async Task<JsonDocument> CallAsync(string workspace, string method, IEnumerable<(string Key, string? Value)> parameters, CancellationToken ct)
    {
        var token = Workspace(workspace).Token;
        var form = parameters.Where(p => p.Value is not null).Select(p => new KeyValuePair<string, string>(p.Key, p.Value!)).ToList();

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, method) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                if (attempt > 0 || wait > MaxRetryWait)
                    throw new McpException($"Slack is rate-limiting {method}; try again in {Math.Ceiling(wait.TotalSeconds)} seconds");
                await Task.Delay(wait, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new McpException($"Slack returned HTTP {(int)response.StatusCode} for {method}");

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return doc;

            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown_error";
            var needed = doc.RootElement.TryGetProperty("needed", out var n) ? n.GetString() : null;
            doc.Dispose();
            throw new McpException(Describe(workspace, method, error, needed));
        }
    }

    private static string Describe(string workspace, string method, string? error, string? needed) =>
        $"Slack {method} failed in workspace '{workspace}': {error}." + error switch
        {
            "missing_scope" => $" The app is missing the {needed ?? "required"} scope; re-create it from deploy/slack/manifest and re-run deploy/slack/setup.sh.",
            "invalid_auth" or "not_authed" or "token_revoked" or "token_expired" or "account_inactive" =>
                " The token was rejected; re-run deploy/slack/setup.sh for this workspace.",
            "channel_not_found" => " Check the channel id or name with list_conversations.",
            "not_in_channel" => " Only conversations you are a member of can be read.",
            "thread_not_found" => " Check the thread's ts with get_history.",
            "user_not_found" => " Pass a user id such as U0123ABCD.",
            _ => ""
        };
}
