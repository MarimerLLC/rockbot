using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace RockBot.UserProxy.AgUi;

/// <summary>Where a client fetches an attachment's bytes from this proxy.</summary>
internal static class AttachmentLinks
{
    public const string Route = "/attachments";

    /// <summary>
    /// The URL of <c>GET /attachments/{path}</c> for an attachment, relative to the proxy's root:
    /// <c>/attachments/previews/ab12/deck.html</c>. Each path segment is escaped.
    /// </summary>
    public static string Url(string path) =>
        Route + "/" + string.Join('/', path.Split('/', '\\').Where(s => s.Length > 0).Select(Uri.EscapeDataString));

    public static string Url(AgentAttachment attachment) => Url(attachment.Path);
}

/// <summary>
/// The proxy's plain REST endpoints (#794, phase 4): what a RockBot client does besides running
/// turns. Each is one call on <see cref="UserProxyService"/>; none of it is AG-UI.
/// </summary>
internal static class AgUiRestEndpoints
{
    /// <summary>The MCP sign-in redirect URI's path. Registered with each MCP server's OAuth client.</summary>
    public const string SignInCallbackPath = "/mcp-oauth/callback";

    /// <summary>Room for a multipart body's boundaries and field headers around the file itself.</summary>
    private const long UploadFramingAllowance = 64 * 1024;

    /// <summary>The largest JSON body a REST endpoint reads. A saved response is a chat reply, not a file.</summary>
    private const long MaxJsonBodyBytes = 1024 * 1024;

    /// <summary>
    /// Maps the endpoints a client calls, onto <paramref name="api"/>: the group that app auth
    /// (phase 5) will cover. The sign-in redirect is not among them; see
    /// <see cref="MapMcpSignInCallback"/>.
    /// </summary>
    public static void MapAgUiRest(this IEndpointRouteBuilder api)
    {
        api.MapPost(AttachmentLinks.Route, UploadAttachmentAsync);
        api.MapGet(AttachmentLinks.Route + "/{**path}", ServeAttachment);

        api.MapPost("/feedback", FeedbackAsync);

        api.MapGet("/saved", ListSavedAsync);
        api.MapPost("/saved", SaveAsync);
        api.MapGet("/saved/{id}", GetSavedAsync);
        api.MapDelete("/saved/{id}", DeleteSavedAsync);

        api.MapGet("/mcp-signin", ListSignInsAsync);
        api.MapPost("/mcp-signin/{server}/start", StartSignInAsync);

        api.MapPost("/threads/{id}/clear", ClearAsync);
        api.MapGet("/threads/{id}/questions", Questions);
        api.MapGet("/agent", AgentAsync);
    }

    // ── Attachments ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>POST /attachments</c>: hands a file to the agent, which writes it to the shared volume,
    /// and returns the reference to put in a run's <c>forwardedProps.rockbot.attachments</c>. The
    /// body is <c>multipart/form-data</c> with a <c>file</c> part (what a browser's
    /// <c>FormData</c> and .NET's <c>MultipartFormDataContent</c> send), or the file itself, with
    /// its type as <c>Content-Type</c> and its name in the <c>fileName</c> query parameter.
    /// </summary>
    /// <remarks>
    /// The proxy never writes the file: it mounts the volume read-only, and the agent's
    /// <c>InboundAttachmentService</c> is the one place that picks the stored name and enforces
    /// the accepted types. What is refused here is what can be refused without the agent: a file
    /// over the agent's size limit, before its bytes are read, and a declared type that isn't the
    /// type of the file's extension.
    /// </remarks>
    private static async Task<IResult> UploadAttachmentAsync(
        HttpContext context, UserProxyService proxy, AgUiOptions options, CancellationToken ct)
    {
        var request = context.Request;
        const long max = AttachmentUploadRequest.MaxBytes;

        // The server stops reading at the limit; without this it would take a body of its own
        // default limit (30 MB) into memory before the length check below ever ran.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = max + UploadFramingAllowance;
        if (request.ContentLength > max + UploadFramingAllowance)
            return TooLarge();

        string? threadId = request.Query["threadId"];
        string? fileName = request.Query["fileName"];
        string? mime;
        byte[]? data;
        try
        {
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(ct);
                if ((form.Files.GetFile("file") ?? form.Files.FirstOrDefault()) is not { } file)
                    return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, "The form has no file part. Send the file in a part named \"file\".");
                if (file.Length > max)
                    return TooLarge();

                if (string.IsNullOrEmpty(threadId))
                    threadId = form["threadId"];
                if (string.IsNullOrEmpty(fileName))
                    fileName = file.FileName;
                mime = file.ContentType;

                data = new byte[file.Length];
                await using var stream = file.OpenReadStream();
                await stream.ReadExactlyAsync(data, ct);
            }
            else
            {
                mime = request.ContentType;
                data = await ReadAtMostAsync(request.Body, max, ct);
                if (data is null)
                    return TooLarge();
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, $"The body could not be read as an upload: {ex.Message}");
        }

        if (ResolveThread(threadId, options, out var thread) is { } refusal)
            return refusal;

        // The name only: whatever directory the client's own file system had it in is not ours.
        fileName = LeafName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest,
                "The file has no name. Give the file part a filename, or pass the fileName query parameter.");
        }
        if (data.Length == 0)
            return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, "The file is empty.");

        // One table says what type a file name has; a declared type has to agree with it. A
        // client that can't say (a browser sends application/octet-stream for a type it doesn't
        // know) gets the table's answer.
        var expected = AttachmentMimeTypes.FromFileName(fileName);
        var declared = BareMediaType(mime);
        if (expected == AttachmentMimeTypes.Unknown)
        {
            return AgUiEndpoints.Refusal(StatusCodes.Status415UnsupportedMediaType,
                $"'{fileName}' is not a type of file that can be attached.");
        }
        if (declared is not (null or AttachmentMimeTypes.Unknown) && !string.Equals(declared, expected, StringComparison.OrdinalIgnoreCase))
        {
            return AgUiEndpoints.Refusal(StatusCodes.Status415UnsupportedMediaType,
                $"'{fileName}' was sent as {declared}, but its extension says {expected}.");
        }

        if (!proxy.IsConnected)
            return NotConnected();

        var response = await proxy.UploadAttachmentAsync(
            new AttachmentUploadRequest
            {
                FileName = fileName, Mime = expected, Data = data, SessionId = thread, UserId = options.DefaultUserId,
            },
            options.RequestTimeout, ct);

        if (response is null)
            return NoAnswer("the upload");
        if (!response.Success || response.Attachment is not { } stored)
        {
            // The agent's own refusal (a type it doesn't take, say), in words meant for the user.
            return AgUiEndpoints.Refusal(StatusCodes.Status422UnprocessableEntity,
                response.Error ?? "The agent did not accept the file.");
        }

        var url = AttachmentLinks.Url(stored);
        context.Response.Headers.Location = url;
        return Json(new { stored.Mime, stored.Path, stored.FileName, url }, StatusCodes.Status201Created);
    }

    /// <summary>
    /// <c>GET /attachments/{path}</c>: the bytes of a file under the shared attachments directory,
    /// which the proxy mounts read-only. The path is the one an attachment reference carries
    /// (<see cref="AgentAttachment.Path"/>); <see cref="AttachmentPathResolver"/> keeps it inside
    /// that directory.
    /// </summary>
    /// <remarks>
    /// The files were written by the agent, a script or a remote MCP server, so they are served as
    /// untrusted content, exactly as the Blazor UI serves them: the declared type is the only one a
    /// browser may act on (<c>nosniff</c>), and a type that can carry script (HTML, SVG) is
    /// confined to an opaque origin by a <c>sandbox</c> policy, so opened in a browser it can't
    /// reach anything else served from this host.
    /// </remarks>
    private static IResult ServeAttachment(string? path, HttpContext context, AttachmentPathResolver resolver)
    {
        if (resolver.Resolve(path) is not { } resolved)
            return AgUiEndpoints.Refusal(StatusCodes.Status404NotFound, "There is no such attachment.");

        var mime = AttachmentPathResolver.GuessMime(resolved);
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (AttachmentPresentation.ContentSecurityPolicyFor(mime) is { } csp)
            context.Response.Headers.ContentSecurityPolicy = csp;

        return Results.File(resolved, AttachmentPresentation.ContentTypeFor(mime));
    }

    /// <summary>
    /// The first of <paramref name="attachments"/> that isn't a file under the attachments
    /// directory, or null when all are (or when this proxy has no volume to look at: running
    /// outside the cluster, it leaves the check to the agent).
    /// </summary>
    public static AgentAttachment? FirstMissing(AttachmentPathResolver resolver, IReadOnlyList<AgentAttachment>? attachments) =>
        attachments is null || !Directory.Exists(resolver.BasePath)
            ? null
            : attachments.FirstOrDefault(a => resolver.Resolve(a.Path) is null);

    /// <summary>The whole body, or null when it is longer than <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]?> ReadAtMostAsync(Stream body, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string? LeafName(string? fileName)
    {
        if (fileName is null)
            return null;
        var cut = fileName.LastIndexOfAny(['/', '\\']);
        return (cut >= 0 ? fileName[(cut + 1)..] : fileName).Trim();
    }

    private static string? BareMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;
        var semicolon = contentType.IndexOf(';');
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
    }

    private static IResult TooLarge() => AgUiEndpoints.Refusal(StatusCodes.Status413PayloadTooLarge,
        $"The file is over the {AttachmentUploadRequest.MaxBytes / 1024 / 1024} MB limit.");

    // ── Feedback ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>POST /feedback</c>: thumbs up or down on a reply. The body is
    /// <c>{"threadId": "...", "messageId": "...", "isPositive": true}</c>. Answers <c>202</c>: on
    /// a thumbs down the agent tries again, and that reply arrives on the notify stream.
    /// </summary>
    private static async Task<IResult> FeedbackAsync(
        HttpContext context, UserProxyService proxy, UserProxyOptions proxyOptions, AgUiOptions options, CancellationToken ct)
    {
        var (body, invalid) = await ReadObjectAsync(context, ct);
        if (invalid is not null)
            return invalid;

        if (ResolveThread(Str(body, "threadId"), options, out var thread) is { } refusal)
            return refusal;
        if (Str(body, "messageId") is not { } messageId)
            return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, "messageId is required: the id of the reply the feedback is about.");
        if (!body.TryGetProperty("isPositive", out var positive) || positive.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, "isPositive is required: true for a thumbs up, false for a thumbs down.");
        if (!proxy.IsConnected)
            return NotConnected();

        await proxy.SendFeedbackAsync(new UserFeedback
        {
            SessionId = thread,
            MessageId = messageId,
            IsPositive = positive.GetBoolean(),
            AgentName = Str(body, "agentName") ?? proxyOptions.AgentName,
        }, ct);
        return Results.Accepted();
    }

    // ── Saved responses ──────────────────────────────────────────────────────

    /// <summary><c>GET /saved</c>: the saved responses, as <c>{"items": [...]}</c> of summaries.</summary>
    private static async Task<IResult> ListSavedAsync(UserProxyService proxy, AgUiOptions options, CancellationToken ct)
    {
        if (!proxy.IsConnected)
            return NotConnected();
        return await proxy.ListSavedResponsesAsync(options.RequestTimeout, ct) is { } list
            ? Json(list)
            : NoAnswer("the request for the saved responses");
    }

    /// <summary><c>GET /saved/{id}</c>: one saved response with its content. <c>404</c> when there is none.</summary>
    private static async Task<IResult> GetSavedAsync(string id, UserProxyService proxy, AgUiOptions options, CancellationToken ct)
    {
        if (!proxy.IsConnected)
            return NotConnected();
        var saved = await proxy.GetSavedResponseAsync(id, options.RequestTimeout, ct);
        if (saved is null)
            return NoAnswer("the request for the saved response");
        return saved.Found
            ? Json(new { saved.Id, saved.Label, saved.Content, saved.AgentName, saved.SavedAt })
            : AgUiEndpoints.Refusal(StatusCodes.Status404NotFound, $"There is no saved response '{id}'.");
    }

    /// <summary>
    /// <c>POST /saved</c>: saves a reply's text under a label. The body is
    /// <c>{"threadId": "...", "label": "...", "content": "..."}</c>; answers <c>201</c> with the new id.
    /// </summary>
    private static async Task<IResult> SaveAsync(
        HttpContext context, UserProxyService proxy, UserProxyOptions proxyOptions, AgUiOptions options, CancellationToken ct)
    {
        var (body, invalid) = await ReadObjectAsync(context, ct);
        if (invalid is not null)
            return invalid;

        if (ResolveThread(Str(body, "threadId"), options, out var thread) is { } refusal)
            return refusal;
        if (Str(body, "label") is not { } label || string.IsNullOrWhiteSpace(label))
            return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, "label is required.");
        if (Str(body, "content") is not { } content || string.IsNullOrWhiteSpace(content))
            return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, "content is required: the text to save.");
        if (!proxy.IsConnected)
            return NotConnected();

        var ack = await proxy.SaveResponseAsync(
            new SaveResponseRequest
            {
                Label = label.Trim(), Content = content, SessionId = thread,
                AgentName = Str(body, "agentName") ?? proxyOptions.AgentName,
            },
            options.RequestTimeout, ct);

        if (ack is null)
            return NoAnswer("the request to save");
        if (!ack.Success)
            return AgUiEndpoints.Refusal(StatusCodes.Status502BadGateway, "The agent could not save the response.");

        context.Response.Headers.Location = $"/saved/{Uri.EscapeDataString(ack.Id)}";
        return Json(new { ack.Id }, StatusCodes.Status201Created);
    }

    /// <summary><c>DELETE /saved/{id}</c>: <c>204</c>, or <c>404</c> when there was nothing to delete.</summary>
    private static async Task<IResult> DeleteSavedAsync(string id, UserProxyService proxy, AgUiOptions options, CancellationToken ct)
    {
        if (!proxy.IsConnected)
            return NotConnected();
        var ack = await proxy.DeleteSavedResponseAsync(id, options.RequestTimeout, ct);
        if (ack is null)
            return NoAnswer("the request to delete");
        return ack.Success
            ? Results.NoContent()
            : AgUiEndpoints.Refusal(StatusCodes.Status404NotFound, $"There is no saved response '{id}'.");
    }

    // ── MCP sign-in ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET /mcp-signin</c>: the MCP servers that use OAuth sign-in and where each stands, as
    /// <c>{"servers": [{"serverName", "state", "connected"}]}</c>. Changes after that arrive on
    /// the notify stream as <c>rockbot.mcp_signin_status</c>.
    /// </summary>
    private static async Task<IResult> ListSignInsAsync(UserProxyService proxy, AgUiOptions options, CancellationToken ct)
    {
        if (!proxy.IsConnected)
            return NotConnected();
        return await proxy.ListMcpSignInsAsync(options.RequestTimeout, ct) is { } list
            ? Json(list)
            : NoAnswer("the request for the sign-ins");
    }

    /// <summary>
    /// <c>POST /mcp-signin/{server}/start</c>: asks the agent to start a sign-in to an MCP server
    /// and returns the page to open in the user's browser. The authorization server sends the
    /// browser back to this proxy's <see cref="SignInCallbackPath"/>; the outcome arrives on the
    /// notify stream as <c>rockbot.mcp_signin_status</c> with the same <c>signInId</c>.
    /// </summary>
    /// <remarks>
    /// The redirect URI is always this proxy's own callback. A client doesn't choose it: the code
    /// is sent wherever it points.
    /// </remarks>
    private static async Task<IResult> StartSignInAsync(
        string server, HttpContext context, UserProxyService proxy, AgUiOptions options, McpSignInTracker signIns,
        CancellationToken ct)
    {
        if (!proxy.IsConnected)
            return NotConnected();

        var redirectUri = SignInRedirectUri(context.Request, options);
        var response = await proxy.StartMcpSignInAsync(server, redirectUri, options.SignInStartTimeout, ct);

        if (response is null)
            return NoAnswer("the request to start the sign-in");
        if (response.AuthorizationUri is null || response.Error is not null)
        {
            return AgUiEndpoints.Refusal(StatusCodes.Status422UnprocessableEntity,
                response.Error ?? $"The agent could not start a sign-in to '{server}'.");
        }
        if (!signIns.Track(response))
        {
            return AgUiEndpoints.Refusal(StatusCodes.Status502BadGateway,
                $"The authorization URL for '{server}' carries no state, so its redirect could not be matched to this sign-in.");
        }

        return Json(new { response.ServerName, response.SignInId, response.AuthorizationUri, response.ExpiresAt, redirectUri });
    }

    /// <summary>
    /// Where an authorization server sends the browser back: this proxy's callback, at the address
    /// the browser can reach it. That is <see cref="AgUiOptions.PublicBaseUrl"/> when set, and the
    /// address of the request otherwise.
    /// </summary>
    internal static string SignInRedirectUri(HttpRequest request, AgUiOptions options) =>
        (options.ResolvePublicBaseUrl() ?? $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/'))
        + SignInCallbackPath;

    /// <summary>
    /// Maps <see cref="SignInCallbackPath"/>, the browser leg of an MCP sign-in: the redirect URI
    /// an authorization server sends the user back to. It relays what it was given (the code,
    /// state and issuer, or the refusal) to the agent, which holds the PKCE verifier and exchanges
    /// the code itself, and shows a page that says the tab can be closed.
    /// </summary>
    /// <remarks>
    /// Anonymous, and it stays so with app auth: a browser arriving from an authorization server
    /// carries no bearer token. What makes that safe: only a redirect for a sign-in this proxy
    /// started is relayed (<see cref="McpSignInTracker"/>), each once; the code is worthless
    /// without the verifier the agent keeps; and the page shows nothing it was sent except the
    /// error code, encoded.
    /// </remarks>
    public static IEndpointConventionBuilder MapMcpSignInCallback(this IEndpointRouteBuilder app) =>
        app.MapGet(SignInCallbackPath, async (HttpRequest request, UserProxyService proxy, McpSignInTracker signIns, CancellationToken ct) =>
        {
            var query = request.Query;
            var state = query["state"].ToString();
            if (string.IsNullOrEmpty(state))
            {
                return Page(HttpStatusCode.BadRequest, "This sign-in link is incomplete",
                    "The authorization server sent the browser back without a state, so the sign-in it belongs to can't be found. Start the sign-in again.");
            }

            if (signIns.Take(state) is null)
            {
                return Page(HttpStatusCode.BadRequest, "This sign-in is not under way",
                    "No sign-in is waiting for this link: it has expired, was already used, or was not started here. Start the sign-in again.");
            }

            var callback = new McpSignInCallback
            {
                State = state,
                Code = NullIfEmpty(query["code"].ToString()),
                Iss = NullIfEmpty(query["iss"].ToString()),
                Error = NullIfEmpty(query["error"].ToString()),
                ErrorDescription = NullIfEmpty(query["error_description"].ToString()),
            };

            // Relayed even without a code: the agent then fails the sign-in at once, and says so
            // on the notify stream, instead of leaving it to expire.
            await proxy.SendMcpSignInCallbackAsync(callback, ct);

            if (callback.Error is not null)
            {
                return Page(HttpStatusCode.OK, "Sign-in refused",
                    $"The authorization server refused the sign-in ({callback.Error}). You can close this tab; the app shows what happened.");
            }
            return callback.Code is null
                ? Page(HttpStatusCode.BadRequest, "This sign-in link is incomplete",
                    "The authorization server sent the browser back without a code. You can close this tab and start the sign-in again.")
                : Page(HttpStatusCode.OK, "Signed in",
                    "RockBot is finishing the sign-in. You can close this tab and go back to the app.");
        })
        .AllowAnonymous();

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static IResult Page(HttpStatusCode status, string title, string message) =>
        Results.Content(
            $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem;color:#222}</style></head>
            <body><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p></body>
            </html>
            """,
            "text/html; charset=utf-8",
            statusCode: (int)status);

    // ── Threads ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>POST /threads/{id}/clear</c>: clears the thread's conversation context on the agent
    /// (long-term memory is kept). Answers <c>202</c>; the agent confirms with a reply on the
    /// notify stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every client of the thread has to drop what it shows, not only the one that asked, so the
    /// thread's notify stream gets <c>rockbot.context_cleared</c>. It goes out before the request
    /// to the agent, so the agent's confirmation always follows it: a client that clears its view
    /// on the event doesn't wipe the confirmation with it.
    /// </para>
    /// <para>
    /// The proxy's own state for the thread is left as it is. The notify buffer keeps its events:
    /// a client that reconnects replays them in order, the clear among them, and ends up where
    /// everyone else is. Open questions stay until the agent reports them closed or they expire:
    /// they belong to MCP calls rather than to the conversation, and the proxy doesn't guess
    /// which calls the clear stopped. An open run is not touched either; the agent stops the
    /// thread's in-flight work when it clears, and the run ends when the agent reports it
    /// cancelled.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ClearAsync(
        string id, UserProxyService proxy, AgUiOptions options, NotifyHub hub, CancellationToken ct)
    {
        if (!options.IsThreadAllowed(id))
            return ThreadNotAllowed(id);
        if (!proxy.IsConnected)
            return NotConnected();

        hub.PublishToThread(id, m => [m.Custom(TurnEventMapper.CustomNames.ContextCleared, new { threadId = id })]);
        await proxy.ClearContextAsync(id, ct);
        return Results.Accepted();
    }

    /// <summary>
    /// <c>GET /threads/{id}/questions</c>: the MCP questions open on the thread, oldest first, each
    /// in the shape of a <c>rockbot.mcp_question</c> event's value. For a client that connects
    /// fresh, or reloads beyond the notify buffer, and so never saw the events.
    /// </summary>
    /// <remarks>
    /// What the proxy knows, which is what it has heard since it started (see
    /// <see cref="McpQuestionRegistry"/>): after a restart the list is empty though questions may
    /// be open on the agent.
    /// </remarks>
    private static IResult Questions(string id, AgUiOptions options, McpQuestionRegistry questions)
    {
        if (!options.IsThreadAllowed(id))
            return ThreadNotAllowed(id);

        return Json(new
        {
            threadId = id,
            questions = questions.Open(id).Select(q => McpQuestionInterrupts.QuestionValue(q.Reply, q.Form)).ToList(),
        });
    }

    // ── Agent ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET /agent?threadId=</c>: the agent's name and version, and what it is doing now: whether
    /// it is working on the thread's turn, and the thread's running subagents and workers. For a
    /// client starting up, to set its title and its busy indicators.
    /// </summary>
    private static async Task<IResult> AgentAsync(
        string? threadId, UserProxyService proxy, AgUiOptions options, CancellationToken ct)
    {
        if (ResolveThread(threadId, options, out var thread) is { } refusal)
            return refusal;
        if (!proxy.IsConnected)
            return NotConnected();

        var info = proxy.GetAgentInfoAsync(options.RequestTimeout, ct);
        var status = proxy.GetActiveStatusAsync(thread, options.RequestTimeout, ct);
        await Task.WhenAll(info, status);

        if (info.Result is not { } agent || status.Result is not { } active)
            return NoAnswer("the request for its status");

        return Json(new
        {
            agent.AgentName,
            agent.AgentVersion,
            threadId = thread,
            active.IsProcessing,
            // Work the agent attributes to a thread is that thread's; an older agent doesn't say.
            work = (active.Work ?? [])
                .Where(w => w.ThreadId is null || string.Equals(w.ThreadId, thread, StringComparison.Ordinal))
                .ToList(),
        });
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The thread a request is about: the one it names, or the only one there is. Returns the
    /// refusal when it names none and there are several (<c>400</c>), or one that isn't allowed
    /// (<c>403</c>).
    /// </summary>
    private static IResult? ResolveThread(string? stated, AgUiOptions options, out string threadId)
    {
        threadId = string.IsNullOrEmpty(stated) ? (options.ThreadIds() is [var only] ? only : "") : stated;
        if (threadId.Length == 0)
            return AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, "threadId is required.");
        return options.IsThreadAllowed(threadId) ? null : ThreadNotAllowed(threadId);
    }

    private static IResult ThreadNotAllowed(string threadId) =>
        AgUiEndpoints.Refusal(StatusCodes.Status403Forbidden, $"Thread '{threadId}' is not allowed.");

    private static IResult NotConnected() =>
        AgUiEndpoints.Refusal(StatusCodes.Status503ServiceUnavailable, "The proxy is not connected to the message bus.");

    private static IResult NoAnswer(string what) =>
        AgUiEndpoints.Refusal(StatusCodes.Status504GatewayTimeout, $"The agent did not answer {what} in time.");

    /// <summary>
    /// The bus's own JSON shape (camelCase, nulls left out) with the events' relaxed encoder, so a
    /// value reads the same from a REST endpoint as in an event.
    /// </summary>
    private static readonly JsonSerializerOptions RestJson = new(TurnEventMapper.PayloadJson)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static IResult Json(object value, int status = StatusCodes.Status200OK) =>
        Results.Json(value, RestJson, statusCode: status);

    /// <summary>The body as a JSON object, or the <c>400</c> (or <c>413</c>) to answer with.</summary>
    private static async Task<(JsonElement Body, IResult? Invalid)> ReadObjectAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = MaxJsonBodyBytes;
        if (context.Request.ContentLength > MaxJsonBodyBytes)
            return (default, AgUiEndpoints.Refusal(StatusCodes.Status413PayloadTooLarge, "The body is too large."));

        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            return (default, AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, $"The body is not JSON: {ex.Message}"));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (default, AgUiEndpoints.Refusal(StatusCodes.Status413PayloadTooLarge, "The body is too large."));
        }

        return body.ValueKind == JsonValueKind.Object
            ? (body, null)
            : (default, AgUiEndpoints.Refusal(StatusCodes.Status400BadRequest, "The body must be a JSON object."));
    }

    private static string? Str(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
