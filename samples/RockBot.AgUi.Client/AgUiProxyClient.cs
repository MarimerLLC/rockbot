using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using RockBot.UserProxy;

namespace RockBot.AgUi.Client;

/// <summary>How to reach the proxy.</summary>
public sealed class AgUiProxyClientOptions
{
    /// <summary>The proxy's address, for example <c>http://rockbot-agui/</c>.</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>
    /// App mode: the caller's ID token, sent as <c>Authorization: Bearer</c> on every request.
    /// Null, or a provider that returns null, sends none (network mode).
    /// </summary>
    public Func<CancellationToken, ValueTask<string?>>? AccessToken { get; init; }

    /// <summary>
    /// Applied to the two requests whose response is an event stream, before they are sent. A
    /// Blazor WebAssembly app turns browser response streaming on here; without it the browser
    /// hands the response over only when it is complete, which for the notify stream is never.
    /// </summary>
    public Action<HttpRequestMessage>? ConfigureStreamingRequest { get; init; }
}

/// <summary>The proxy refused a request. <see cref="Exception.Message"/> is its problem-details <c>detail</c>.</summary>
public sealed class AgUiProxyException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status => status;
}

/// <summary>What a turn sends: the user's text and the files that go with it.</summary>
public sealed record AgUiRunRequest(string ThreadId, string RunId, string Text)
{
    /// <summary>The message's attachments, as <c>POST /attachments</c> returned them.</summary>
    public IReadOnlyList<AgentAttachment> Attachments { get; init; } = [];

    /// <summary><c>forwardedProps.rockbot.questionMode</c>. Null asks for no question forms.</summary>
    public string? QuestionMode { get; init; } = "custom";

    /// <summary><c>forwardedProps.rockbot.capabilities</c>: what the client can render. Null leaves it to the proxy's default.</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }
}

/// <summary>
/// The HTTP calls a RockBot client makes on the AG-UI proxy (<c>docs/agui.md</c>). Thin on
/// purpose: each method is one request. <see cref="ChatSession"/> is what strings them together.
/// </summary>
public sealed class AgUiProxyClient(HttpClient http, AgUiProxyClientOptions options)
{
    /// <summary>An address on the proxy, from a path relative to it (an attachment's <c>url</c>, say).</summary>
    public Uri Resolve(string relative) => new(options.BaseAddress, relative.TrimStart('/'));

    /// <summary>True when requests carry a bearer token (app mode).</summary>
    public bool UsesAccessToken => options.AccessToken is not null;

    // ── The two streams ──────────────────────────────────────────────────────

    /// <summary>
    /// <c>POST /agui</c>: runs one turn and yields its events as they arrive. Throws
    /// <see cref="AgUiProxyException"/> when the proxy refuses the request before any event.
    /// Cancelling the enumeration closes the connection, which cancels the turn on the agent.
    /// </summary>
    public async IAsyncEnumerable<AgUiFrame> RunAsync(
        AgUiRunRequest run, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve("agui"))
        {
            Content = new StringContent(RunBody(run), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        options.ConfigureStreamingRequest?.Invoke(request);

        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var frame in AgUiSse.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    /// <summary>
    /// <c>GET /agui/notify</c>: the thread's unsolicited output, until the connection ends. With
    /// <paramref name="lastEventId"/> the proxy first replays what followed that event.
    /// </summary>
    public async IAsyncEnumerable<AgUiFrame> NotifyAsync(
        string threadId, string? lastEventId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve($"agui/notify?threadId={Uri.EscapeDataString(threadId)}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(lastEventId))
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        options.ConfigureStreamingRequest?.Invoke(request);

        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var frame in AgUiSse.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            yield return frame;
    }

    /// <summary>The body of a run: an AG-UI <c>RunAgentInput</c> with RockBot's part in <c>forwardedProps.rockbot</c>.</summary>
    internal static string RunBody(AgUiRunRequest run)
    {
        var rockbot = new JsonObject();
        if (run.QuestionMode is { Length: > 0 } mode)
            rockbot["questionMode"] = mode;
        if (run.Capabilities is { Count: > 0 } capabilities)
            rockbot["capabilities"] = new JsonArray([.. capabilities.Select(c => (JsonNode?)JsonValue.Create(c))]);
        if (run.Attachments.Count > 0)
        {
            rockbot["attachments"] = new JsonArray([.. run.Attachments.Select(a =>
            {
                var attachment = new JsonObject { ["mime"] = a.Mime, ["path"] = a.Path };
                if (!string.IsNullOrEmpty(a.FileName))
                    attachment["fileName"] = a.FileName;
                return (JsonNode?)attachment;
            })]);
        }

        // Only the newest user message: the agent keeps the conversation, and the proxy ignores
        // anything a client replays.
        var body = new JsonObject
        {
            ["threadId"] = run.ThreadId,
            ["runId"] = run.RunId,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["id"] = run.RunId + "-user", ["role"] = "user", ["content"] = run.Text,
            }),
            ["forwardedProps"] = new JsonObject { ["rockbot"] = rockbot },
        };
        return body.ToJsonString();
    }

    // ── REST ─────────────────────────────────────────────────────────────────

    /// <summary><c>GET /threads/{id}/messages</c>: the conversation, as one <c>MESSAGES_SNAPSHOT</c> event.</summary>
    public async Task<JsonElement> GetMessagesAsync(string threadId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve($"threads/{Uri.EscapeDataString(threadId)}/messages"));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    /// <summary><c>GET /threads/{id}/questions</c>: the open MCP questions, each the value of a <c>rockbot.mcp_question</c> event.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetQuestionsAsync(string threadId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve($"threads/{Uri.EscapeDataString(threadId)}/questions"));
        var answer = await ReadAsync(request, WireJson.Default.QuestionsResponse, cancellationToken).ConfigureAwait(false);
        return answer?.Questions ?? [];
    }

    /// <summary><c>GET /agent</c>: the agent's name and version, and what it is doing on the thread.</summary>
    public async Task<AgentInfo?> GetAgentAsync(string threadId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve($"agent?threadId={Uri.EscapeDataString(threadId)}"));
        return await ReadAsync(request, WireJson.Default.AgentInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /questions/{id}/answer</c>: answers an MCP question with <paramref name="answers"/>
    /// (field values by field name), or declines it. The outcome arrives on the notify stream.
    /// </summary>
    public async Task AnswerQuestionAsync(
        string questionId, string threadId, JsonObject? answers, bool decline, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["threadId"] = threadId };
        if (decline)
            body["decline"] = true;
        else
            body["answers"] = answers?.DeepClone() ?? new JsonObject();

        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve($"questions/{Uri.EscapeDataString(questionId)}/answer"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>POST /threads/{id}/cancel</c>: asks the agent to stop the thread's work. An open run then ends as cancelled.</summary>
    public async Task CancelAsync(string threadId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve($"threads/{Uri.EscapeDataString(threadId)}/cancel"));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /attachments</c>: uploads a file (the file itself as the body) and returns the
    /// reference to put on a run. The proxy and the agent decide what is accepted.
    /// </summary>
    public async Task<WireAttachment> UploadAsync(
        string threadId, string fileName, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var uri = Resolve($"attachments?threadId={Uri.EscapeDataString(threadId)}&fileName={Uri.EscapeDataString(fileName)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ReadOnlyMemoryContent(content) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type)
            ? type
            : new MediaTypeHeaderValue("application/octet-stream");

        return await ReadAsync(request, WireJson.Default.WireAttachment, cancellationToken).ConfigureAwait(false)
            ?? throw new AgUiProxyException(HttpStatusCode.BadGateway, "The proxy answered the upload with nothing.");
    }

    /// <summary>
    /// <c>POST /tickets</c>: a short-lived URL for an attachment, for a request the browser makes
    /// by itself (<c>&lt;img src&gt;</c>, a link) and so without a token. In network mode the
    /// answer is the plain URL.
    /// </summary>
    public async Task<TicketResponse> AttachmentTicketAsync(string path, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new TicketRequest { Kind = "attachment", Path = path }, WireJson.Default.TicketRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve("tickets"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return await ReadAsync(request, WireJson.Default.TicketResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new AgUiProxyException(HttpStatusCode.BadGateway, "The proxy answered the ticket request with nothing.");
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task<T?> ReadAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(body, type, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends with the bearer token, if any, and turns a refusal into an <see cref="AgUiProxyException"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        if (options.AccessToken is { } provider && await provider(cancellationToken).ConfigureAwait(false) is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            throw new AgUiProxyException(response.StatusCode, await DescribeAsync(response, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>A refusal's reason: the problem-details <c>detail</c> when there is one, else the status.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length > 0 && JsonSerializer.Deserialize(text, WireJson.Default.ProblemBody) is { } problem
                && (problem.Detail ?? problem.Title) is { Length: > 0 } said)
                return said;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            // Not a problem body: the status says what there is to say.
        }

        return $"The proxy answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
    }
}
