using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The plain REST endpoints (#794, phase 4) over the real pipeline: an HTTP client on one side, the
/// fake agent answering the request topics on the in-process bus on the other. The shared
/// attachments volume is a temporary directory.
/// </summary>
[TestClass]
public sealed class AgUiRestEndpointTests
{
    private const string Thread = "agui-session";

    /// <summary>Short enough that a test for an agent that doesn't answer is quick.</summary>
    private static readonly (string, string)[] Impatient =
        [("AgUi:RequestTimeout", "00:00:00.300"), ("AgUi:SignInStartTimeout", "00:00:00.300")];

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private string _sharedRoot = null!;
    private string _attachments = null!;
    private AgUiTestHost _host = null!;
    private HttpClient _client = null!;
    private FakeAgent _agent = null!;

    [TestInitialize]
    public Task Init()
    {
        _sharedRoot = Path.Combine(Path.GetTempPath(), "rb-agui-rest", Guid.NewGuid().ToString("N"));
        _attachments = Path.Combine(_sharedRoot, "attachments");
        Directory.CreateDirectory(Path.Combine(_attachments, "previews", "abc12345"));
        File.WriteAllBytes(Path.Combine(_attachments, "chart.png"), Png);
        File.WriteAllText(Path.Combine(_attachments, "report.pdf"), "%PDF-1.7");
        File.WriteAllText(Path.Combine(_attachments, "diagram.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        File.WriteAllText(Path.Combine(_attachments, "previews", "abc12345", "deck.html"),
            "<!doctype html><html><body><script>document.title='ran'</script></body></html>");
        // Outside the attachments directory, where nothing may be served from.
        File.WriteAllBytes(Path.Combine(_sharedRoot, "secret.png"), Png);
        return StartAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await StopAsync();
        try { Directory.Delete(_sharedRoot, recursive: true); } catch { /* best effort */ }
    }

    private async Task StartAsync(params (string Key, string Value)[] settings)
    {
        _host = new AgUiTestHost(settings)
        {
            ConfigureServices = services => services.AddSingleton(new AttachmentPathResolver(_attachments)),
        };
        _client = _host.CreateClient();
        _agent = await FakeAgent.StartAsync(_host);
    }

    private async Task StopAsync()
    {
        await _agent.DisposeAsync();
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private async Task RestartAsync(params (string Key, string Value)[] settings)
    {
        await StopAsync();
        await StartAsync(settings);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? detailContains = null)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType, text);
        if (detailContains is not null)
            StringAssert.Contains(JsonDocument.Parse(text).RootElement.GetProperty("detail").GetString(), detailContains);
    }

    /// <summary>Gives the agent a moment to receive something it must not.</summary>
    private static Task SettleAsync() => Task.Delay(150);

    // ── Upload ───────────────────────────────────────────────────────────────

    private static MultipartFormDataContent Form(byte[] data, string fileName, string? mime, string? threadId = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        if (mime is not null)
            file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", fileName);
        if (threadId is not null)
            form.Add(new StringContent(threadId), "threadId");
        return form;
    }

    /// <summary>The agent stores what it is sent, under a name of its own choosing.</summary>
    private void AgentStoresUploads() => _agent.OnUpload = request => new AttachmentUploadResponse
    {
        Success = true,
        Attachment = new AgentAttachment { Mime = request.Mime, Path = "stored-" + request.FileName, FileName = request.FileName },
    };

    [TestMethod]
    public async Task Upload_Multipart_HandsTheFileToTheAgent_AndReturnsItsReference()
    {
        AgentStoresUploads();

        using var response = await _client.PostAsync("/attachments", Form(Png, "screen shot.png", "image/png", Thread));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await BodyAsync(response);
        Assert.AreEqual("stored-screen shot.png", body.GetProperty("path").GetString());
        Assert.AreEqual("image/png", body.GetProperty("mime").GetString());
        Assert.AreEqual("screen shot.png", body.GetProperty("fileName").GetString());
        Assert.AreEqual("/attachments/stored-screen%20shot.png", body.GetProperty("url").GetString());
        Assert.AreEqual("/attachments/stored-screen%20shot.png", response.Headers.Location?.OriginalString);

        var upload = _agent.Uploads.Single();
        CollectionAssert.AreEqual(Png, upload.Data);
        Assert.AreEqual("screen shot.png", upload.FileName);
        Assert.AreEqual("image/png", upload.Mime);
        Assert.AreEqual(Thread, upload.SessionId);
        Assert.AreEqual("agui-user", upload.UserId);
    }

    [TestMethod]
    public async Task Upload_RawBody_TakesTheNameFromTheQueryAndTheTypeFromTheContentType()
    {
        AgentStoresUploads();
        var content = new ByteArrayContent(Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var response = await _client.PostAsync("/attachments?threadId=agui-session&fileName=photo.png", content);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("stored-photo.png", (await BodyAsync(response)).GetProperty("path").GetString());
        var upload = _agent.Uploads.Single();
        CollectionAssert.AreEqual(Png, upload.Data);
        Assert.AreEqual("photo.png", upload.FileName);
    }

    [TestMethod]
    public async Task Upload_WithoutAThreadId_UsesTheOnlyThread()
    {
        AgentStoresUploads();

        using var response = await _client.PostAsync("/attachments", Form(Png, "a.png", "image/png"));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(Thread, _agent.Uploads.Single().SessionId);
    }

    [TestMethod]
    public async Task Upload_ATypeTheBrowserCouldNotName_GetsTheTypeOfItsExtension()
    {
        AgentStoresUploads();

        using var response = await _client.PostAsync("/attachments", Form(Png, "a.png", "application/octet-stream"));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("image/png", _agent.Uploads.Single().Mime);
    }

    [TestMethod]
    public async Task Upload_KeepsOnlyTheLeafOfAClientPath()
    {
        AgentStoresUploads();
        var content = new ByteArrayContent(Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var response = await _client.PostAsync(
            "/attachments?fileName=" + Uri.EscapeDataString(@"..\..\etc/../x/photo.png"), content);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("photo.png", _agent.Uploads.Single().FileName);
    }

    [TestMethod]
    public async Task Upload_OverTheSizeLimit_Is413_AndTheAgentIsNotAsked()
    {
        AgentStoresUploads();
        var tooBig = new byte[AttachmentUploadRequest.MaxBytes + 1];

        using var multipart = await _client.PostAsync("/attachments", Form(tooBig, "big.png", "image/png"));
        var raw = new ByteArrayContent(tooBig);
        raw.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var rawResponse = await _client.PostAsync("/attachments?fileName=big.png", raw);

        await AssertProblemAsync(multipart, HttpStatusCode.RequestEntityTooLarge, "8 MB");
        await AssertProblemAsync(rawResponse, HttpStatusCode.RequestEntityTooLarge);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Uploads.Count);
    }

    [TestMethod]
    public async Task Upload_AtTheSizeLimit_IsAccepted()
    {
        AgentStoresUploads();

        using var response = await _client.PostAsync(
            "/attachments", Form(new byte[AttachmentUploadRequest.MaxBytes], "big.png", "image/png"));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(AttachmentUploadRequest.MaxBytes, _agent.Uploads.Single().Data.LongLength);
    }

    [TestMethod]
    [DataRow("notes.txt", "text/plain", DisplayName = "an extension that can't be attached")]
    [DataRow("noextension", "image/png", DisplayName = "no extension")]
    [DataRow("page.png", "text/html", DisplayName = "a type that isn't the extension's")]
    [DataRow("page.html", "image/png", DisplayName = "an extension that isn't the type's")]
    public async Task Upload_WithATypeThatDoesNotFitTheName_Is415_AndTheAgentIsNotAsked(string fileName, string mime)
    {
        AgentStoresUploads();

        using var response = await _client.PostAsync("/attachments", Form(Png, fileName, mime));

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Uploads.Count);
    }

    [TestMethod]
    public async Task Upload_TheAgentRefuses_Is422_WithTheAgentsReason()
    {
        // The agent's own allowlist is narrower than the table of types a reply can carry.
        _agent.OnUpload = _ => new AttachmentUploadResponse { Success = false, Error = "Files of type 'image/svg+xml' are not accepted." };

        using var response = await _client.PostAsync("/attachments", Form(Png, "drawing.svg", "image/svg+xml"));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "not accepted");
    }

    [TestMethod]
    public async Task Upload_WhenTheAgentDoesNotAnswer_Is504()
    {
        await RestartAsync(Impatient);

        using var response = await _client.PostAsync("/attachments", Form(Png, "a.png", "image/png"));

        await AssertProblemAsync(response, HttpStatusCode.GatewayTimeout);
    }

    [TestMethod]
    public async Task Upload_ForAThreadThatIsNotAllowed_Is403()
    {
        AgentStoresUploads();

        using var response = await _client.PostAsync("/attachments", Form(Png, "a.png", "image/png", "blazor-session"));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Uploads.Count);
    }

    [TestMethod]
    public async Task Upload_WithNoFile_NoName_OrNoBytes_Is400()
    {
        AgentStoresUploads();
        var noFile = new MultipartFormDataContent { { new StringContent(Thread), "threadId" } };
        var noName = new ByteArrayContent(Png);
        noName.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var first = await _client.PostAsync("/attachments", noFile);
        using var second = await _client.PostAsync("/attachments", noName);
        using var third = await _client.PostAsync("/attachments", Form([], "empty.png", "image/png"));

        await AssertProblemAsync(first, HttpStatusCode.BadRequest, "file");
        await AssertProblemAsync(second, HttpStatusCode.BadRequest, "name");
        await AssertProblemAsync(third, HttpStatusCode.BadRequest, "empty");
    }

    [TestMethod]
    public async Task Upload_WithSeveralThreads_NeedsAThreadId()
    {
        await RestartAsync(("AgUi:AllowedThreadIds:0", "one"), ("AgUi:AllowedThreadIds:1", "two"));
        AgentStoresUploads();

        using var without = await _client.PostAsync("/attachments", Form(Png, "a.png", "image/png"));
        using var with = await _client.PostAsync("/attachments", Form(Png, "a.png", "image/png", "two"));

        await AssertProblemAsync(without, HttpStatusCode.BadRequest, "threadId");
        Assert.AreEqual(HttpStatusCode.Created, with.StatusCode);
        Assert.AreEqual("two", _agent.Uploads.Single().SessionId);
    }

    // ── Serving ──────────────────────────────────────────────────────────────

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

    [TestMethod]
    public async Task Attachment_IsServedWithItsType_AndMayNotBeSniffed()
    {
        using var response = await _client.GetAsync("/attachments/chart.png");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("image/png", response.Content.Headers.ContentType?.ToString());
        CollectionAssert.AreEqual(Png, await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.AreEqual("", Header(response, "Content-Security-Policy"), "an image carries no script to confine");
    }

    [TestMethod]
    public async Task Attachment_InASubdirectory_IsServedByItsPath()
    {
        using var response = await _client.GetAsync("/attachments/previews/abc12345/deck.html");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task HtmlAttachment_IsConfinedToAnOpaqueOrigin_AndDeclaredUtf8()
    {
        using var response = await _client.GetAsync("/attachments/previews/abc12345/deck.html");

        Assert.AreEqual("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
        // Scripts may run, but with no allow-same-origin: nothing else on this host is reachable.
        Assert.AreEqual("sandbox allow-scripts", Header(response, "Content-Security-Policy"));
    }

    [TestMethod]
    public async Task SvgAttachment_IsFullySandboxed()
    {
        using var response = await _client.GetAsync("/attachments/diagram.svg");

        Assert.AreEqual("image/svg+xml", response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.AreEqual("sandbox", Header(response, "Content-Security-Policy"));
    }

    [TestMethod]
    public async Task PdfAttachment_IsNotSandboxed_SoTheBrowsersViewerWorks()
    {
        using var response = await _client.GetAsync("/attachments/report.pdf");

        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.AreEqual("", Header(response, "Content-Security-Policy"));
    }

    [TestMethod]
    public async Task Attachment_OutsideTheDirectory_IsNotServed()
    {
        var outside = Path.Combine(_sharedRoot, "secret.png");
        string[] attempts =
        [
            "/attachments/..%2Fsecret.png",                       // an encoded slash survives URL normalisation
            "/attachments/..%5Csecret.png",                       // and a backslash
            "/attachments/%2E%2E/secret.png",
            "/attachments/previews/..%2F..%2F..%2Fsecret.png",
            "/attachments/previews%2F..%2F..%2Fsecret.png",
            "/attachments/" + Uri.EscapeDataString(outside),      // an absolute path
            "/attachments/" + Uri.EscapeDataString("/" + outside.Replace('\\', '/')),
            "/attachments/" + Uri.EscapeDataString("attachments/../../secret.png"),
        ];

        foreach (var attempt in attempts)
        {
            using var response = await _client.GetAsync(attempt);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, attempt);
            CollectionAssert.AreNotEqual(Png, await response.Content.ReadAsByteArrayAsync(), attempt);
        }
    }

    [TestMethod]
    public async Task Attachment_ThatDoesNotExist_Is404()
    {
        using var missing = await _client.GetAsync("/attachments/nope.png");
        using var directory = await _client.GetAsync("/attachments/previews");
        using var nothing = await _client.GetAsync("/attachments/");

        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, directory.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, nothing.StatusCode);
    }

    [TestMethod]
    public async Task Attachments_CannotBeWrittenOrDeletedOverHttp()
    {
        using var put = await _client.PutAsync("/attachments/chart.png", new ByteArrayContent([1]));
        using var delete = await _client.DeleteAsync("/attachments/chart.png");

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        CollectionAssert.AreEqual(Png, File.ReadAllBytes(Path.Combine(_attachments, "chart.png")));
    }

    // ── Attachments on a run ─────────────────────────────────────────────────

    private static StringContent RunWith(string attachmentsJson, string text = "What is this?", string runId = "run-1") => Json(
        "{\"threadId\":\"agui-session\",\"runId\":\"" + runId + "\","
        + "\"messages\":[{\"id\":\"m1\",\"role\":\"user\",\"content\":\"" + text + "\"}],"
        + "\"forwardedProps\":{\"rockbot\":{\"attachments\":" + attachmentsJson + "}}}");

    [TestMethod]
    public async Task Run_WithAttachments_SendsTheAgentTheReferences()
    {
        using var response = await _client.PostAsync("/agui", RunWith(
            """[{"path":"chart.png","mime":"image/png","fileName":"My chart.png","url":"/attachments/chart.png"},"""
            + """{"path":"previews/abc12345/deck.html","mime":"text/html"}]"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var message = _agent.Messages.Single().Message;
        Assert.AreEqual("What is this?", message.Content);
        Assert.AreEqual(2, message.Attachments!.Count);
        Assert.AreEqual(new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "My chart.png" }, message.Attachments[0]);
        Assert.AreEqual(new AgentAttachment { Mime = "text/html", Path = "previews/abc12345/deck.html" }, message.Attachments[1]);
    }

    [TestMethod]
    public async Task Run_WithAnUploadsResponse_RoundTrips()
    {
        // What a client does: upload, then hand the response back as it came.
        _agent.OnUpload = request => new AttachmentUploadResponse
        {
            Success = true, Attachment = new AgentAttachment { Mime = request.Mime, Path = "chart.png", FileName = request.FileName },
        };
        using var uploaded = await _client.PostAsync("/attachments", Form(Png, "holiday.png", "image/png"));
        var reference = await uploaded.Content.ReadAsStringAsync();

        using var response = await _client.PostAsync("/agui", RunWith($"[{reference}]"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(
            new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "holiday.png" },
            _agent.Messages.Single().Message.Attachments!.Single());
    }

    [TestMethod]
    public async Task Run_WithAttachmentsAndNoText_IsSent()
    {
        using var response = await _client.PostAsync("/agui", RunWith("""[{"path":"chart.png","mime":"image/png"}]""", text: ""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var message = _agent.Messages.Single().Message;
        Assert.AreEqual("", message.Content);
        Assert.AreEqual("chart.png", message.Attachments!.Single().Path);
    }

    [TestMethod]
    public async Task Run_WithoutAttachments_SendsNone()
    {
        using var response = await _client.PostAsync("/agui", RunWith("null"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNull(_agent.Messages.Single().Message.Attachments);
    }

    [TestMethod]
    [DataRow("""[{"path":"../secret.png","mime":"image/png"}]""", DisplayName = "traversal")]
    [DataRow("""[{"path":"previews/../../secret.png","mime":"image/png"}]""", DisplayName = "traversal inside")]
    [DataRow("""[{"path":"/rockbot/shared/secret.png","mime":"image/png"}]""", DisplayName = "absolute")]
    [DataRow("""[{"path":"C:/Windows/win.png","mime":"image/png"}]""", DisplayName = "drive")]
    [DataRow("""[{"path":"..\\secret.png","mime":"image/png"}]""", DisplayName = "backslash")]
    [DataRow("""[{"path":"//server/share/x.png","mime":"image/png"}]""", DisplayName = "UNC")]
    [DataRow("""[{"path":"chart.png","mime":"text/html"}]""", DisplayName = "a type that isn't the file's")]
    [DataRow("""[{"path":"chart.png"}]""", DisplayName = "no type")]
    [DataRow("""[{"path":"notes.txt","mime":"text/plain"}]""", DisplayName = "a type that can't be attached")]
    [DataRow("""[{"mime":"image/png"}]""", DisplayName = "no path")]
    [DataRow("""["chart.png"]""", DisplayName = "not an object")]
    [DataRow("""{"path":"chart.png","mime":"image/png"}""", DisplayName = "not an array")]
    [DataRow("""[{"path":"nope.png","mime":"image/png"}]""", DisplayName = "no such file")]
    public async Task Run_WithABadAttachmentReference_Is400_AndNothingIsSent(string attachments)
    {
        using var response = await _client.PostAsync("/agui", RunWith(attachments));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Messages.Count);
    }

    [TestMethod]
    public async Task Run_WithTooManyAttachments_Is400()
    {
        var many = string.Join(",", Enumerable.Repeat("""{"path":"chart.png","mime":"image/png"}""", AgUiRequestMapper.MaxAttachments + 1));

        using var response = await _client.PostAsync("/agui", RunWith($"[{many}]"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "more than");
    }

    // ── Attachment URLs on what the agent sends ──────────────────────────────

    private static AgentReply ReplyWithAttachments(string sessionId = Thread) => new()
    {
        Content = "Here it is.", SessionId = sessionId, AgentName = AgUiTestHost.AgentName, IsFinal = true,
        Attachments =
        [
            new AgentAttachment { Mime = "image/png", Path = "chart.png" },
            new AgentAttachment { Mime = "text/html", Path = "previews/abc12345/my deck.html", FileName = "deck.html" },
        ],
    };

    private static void AssertAttachmentsWithUrls(JsonElement attachments)
    {
        Assert.AreEqual(2, attachments.GetArrayLength());
        Assert.AreEqual("chart.png", attachments[0].GetProperty("path").GetString());
        Assert.AreEqual("image/png", attachments[0].GetProperty("mime").GetString());
        Assert.AreEqual("/attachments/chart.png", attachments[0].GetProperty("url").GetString());
        Assert.AreEqual("deck.html", attachments[1].GetProperty("fileName").GetString());
        Assert.AreEqual("/attachments/previews/abc12345/my%20deck.html", attachments[1].GetProperty("url").GetString());
    }

    [TestMethod]
    public async Task ARunsAttachmentsEvent_GivesEachAttachmentAUrl_ThatServesIt()
    {
        _agent.OnMessage = (agent, received) => agent.SendAsync(received, ReplyWithAttachments());

        using var response = await _client.PostAsync("/agui", Json(
            """{"threadId":"agui-session","runId":"run-1","messages":[{"id":"m1","role":"user","content":"Chart it"}]}"""));
        var events = Sse.Events(await response.Content.ReadAsStringAsync());

        var custom = events.Single(e => Sse.Str(e, "name") == TurnEventMapper.CustomNames.Attachments).GetProperty("value");
        AssertAttachmentsWithUrls(custom.GetProperty("attachments"));
        Assert.AreEqual(
            Sse.Str(events.First(e => Sse.Type(e) == "TEXT_MESSAGE_START"), "messageId"),
            custom.GetProperty("messageId").GetString());

        using var served = await _client.GetAsync(custom.GetProperty("attachments")[0].GetProperty("url").GetString());
        CollectionAssert.AreEqual(Png, await served.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    public async Task TheNotifyStreamsAttachmentsEvent_GivesEachAttachmentAUrl()
    {
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        await _agent.BroadcastAsync(ReplyWithAttachments());

        var frames = await stream.ReadAsync(4);
        Assert.AreEqual(TurnEventMapper.CustomNames.Attachments, frames[3].Name);
        AssertAttachmentsWithUrls(frames[3].Value.GetProperty("attachments"));
    }

    [TestMethod]
    public async Task History_GivesEachAttachmentAUrl()
    {
        _agent.History = new ConversationHistoryResponse
        {
            Turns =
            [
                new ConversationHistoryTurn
                {
                    Role = "assistant", Content = "Here it is.", Timestamp = DateTimeOffset.UtcNow,
                    Attachments = ReplyWithAttachments().Attachments,
                },
            ],
        };

        using var response = await _client.GetAsync("/threads/agui-session/messages");

        var message = (await BodyAsync(response)).GetProperty("messages")[0];
        AssertAttachmentsWithUrls(message.GetProperty("metadata").GetProperty("rockbot").GetProperty("attachments"));
    }

    // ── Feedback ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Feedback_IsPassedToTheAgent()
    {
        using var response = await _client.PostAsync("/feedback", Json(
            """{"threadId":"agui-session","messageId":"msg-7","isPositive":false}"""));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        var feedback = await _agent.ReceivedAsync<UserFeedback>();
        Assert.AreEqual(Thread, feedback.SessionId);
        Assert.AreEqual("msg-7", feedback.MessageId);
        Assert.IsFalse(feedback.IsPositive);
        Assert.AreEqual(AgUiTestHost.AgentName, feedback.AgentName);
    }

    [TestMethod]
    public async Task Feedback_CarriesTheAgentNameTheClientGives()
    {
        using var response = await _client.PostAsync("/feedback", Json(
            """{"messageId":"msg-7","isPositive":true,"agentName":"Research"}"""));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var feedback = await _agent.ReceivedAsync<UserFeedback>();
        Assert.IsTrue(feedback.IsPositive);
        Assert.AreEqual("Research", feedback.AgentName);
        Assert.AreEqual(Thread, feedback.SessionId, "the only thread there is");
    }

    [TestMethod]
    [DataRow("""{"isPositive":true}""", DisplayName = "no message id")]
    [DataRow("""{"messageId":"m"}""", DisplayName = "no rating")]
    [DataRow("""{"messageId":"m","isPositive":"yes"}""", DisplayName = "a rating that isn't a boolean")]
    [DataRow("""["m"]""", DisplayName = "not an object")]
    [DataRow("""{"messageId":""", DisplayName = "not JSON")]
    public async Task Feedback_ThatIsIncomplete_Is400(string body)
    {
        using var response = await _client.PostAsync("/feedback", Json(body));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Received<UserFeedback>().Count);
    }

    [TestMethod]
    public async Task Feedback_ForAThreadThatIsNotAllowed_Is403()
    {
        using var response = await _client.PostAsync("/feedback", Json(
            """{"threadId":"blazor-session","messageId":"m","isPositive":true}"""));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Received<UserFeedback>().Count);
    }

    // ── Saved responses ──────────────────────────────────────────────────────

    private static readonly DateTimeOffset SavedAt = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Saved_List_ReturnsTheSummaries()
    {
        _agent.Saved = new ListSavedResponsesResponse
        {
            Items =
            [
                new SavedResponseSummary("s1", "Vendor comparison", "RockBot", SavedAt, "user"),
                new SavedResponseSummary("s2", "Weekly report", "RockBot", SavedAt.AddDays(-1), "scheduled"),
            ],
        };

        using var response = await _client.GetAsync("/saved");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var items = (await BodyAsync(response)).GetProperty("items");
        Assert.AreEqual(2, items.GetArrayLength());
        Assert.AreEqual("s1", items[0].GetProperty("id").GetString());
        Assert.AreEqual("Vendor comparison", items[0].GetProperty("label").GetString());
        Assert.AreEqual("RockBot", items[0].GetProperty("agentName").GetString());
        Assert.AreEqual(SavedAt, items[0].GetProperty("savedAt").GetDateTimeOffset());
        Assert.AreEqual("scheduled", items[1].GetProperty("source").GetString());
    }

    [TestMethod]
    public async Task Saved_Get_ReturnsTheContent()
    {
        _agent.OnGetSaved = id => new GetSavedResponseResponse
        {
            Id = id, Label = "Vendor comparison", Content = "| Vendor | Price |", AgentName = "RockBot", SavedAt = SavedAt,
        };

        using var response = await _client.GetAsync("/saved/s1");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.AreEqual("s1", body.GetProperty("id").GetString());
        Assert.AreEqual("| Vendor | Price |", body.GetProperty("content").GetString());
        Assert.AreEqual("Vendor comparison", body.GetProperty("label").GetString());
        Assert.IsFalse(body.TryGetProperty("found", out _), "the status says whether it was found");
        Assert.AreEqual("s1", _agent.Received<GetSavedResponseRequest>().Single().Id);
    }

    [TestMethod]
    public async Task Saved_Get_OfOneThatIsNotThere_Is404()
    {
        _agent.OnGetSaved = id => new GetSavedResponseResponse
        {
            Id = id, Label = "", Content = "", AgentName = "", SavedAt = default, Found = false,
        };

        using var response = await _client.GetAsync("/saved/gone");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "gone");
    }

    [TestMethod]
    public async Task Saved_Save_ReturnsTheNewId()
    {
        _agent.OnSave = _ => new SaveResponseAck { Id = "s9", Success = true };

        using var response = await _client.PostAsync("/saved", Json(
            """{"threadId":"agui-session","label":" Vendor comparison ","content":"| Vendor | Price |"}"""));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("s9", (await BodyAsync(response)).GetProperty("id").GetString());
        Assert.AreEqual("/saved/s9", response.Headers.Location?.OriginalString);

        var saved = _agent.Received<SaveResponseRequest>().Single();
        Assert.AreEqual("Vendor comparison", saved.Label);
        Assert.AreEqual("| Vendor | Price |", saved.Content);
        Assert.AreEqual(Thread, saved.SessionId);
        Assert.AreEqual(AgUiTestHost.AgentName, saved.AgentName);
    }

    [TestMethod]
    public async Task Saved_Save_ThatTheAgentCouldNotDo_Is502()
    {
        _agent.OnSave = _ => new SaveResponseAck { Id = "", Success = false };

        using var response = await _client.PostAsync("/saved", Json("""{"label":"x","content":"y"}"""));

        await AssertProblemAsync(response, HttpStatusCode.BadGateway);
    }

    [TestMethod]
    [DataRow("""{"content":"y"}""", DisplayName = "no label")]
    [DataRow("""{"label":"  ","content":"y"}""", DisplayName = "a blank label")]
    [DataRow("""{"label":"x"}""", DisplayName = "no content")]
    [DataRow("""{"label":"x","content":7}""", DisplayName = "content that isn't text")]
    [DataRow("""null""", DisplayName = "not an object")]
    public async Task Saved_Save_ThatIsIncomplete_Is400(string body)
    {
        _agent.OnSave = _ => new SaveResponseAck { Id = "s9", Success = true };

        using var response = await _client.PostAsync("/saved", Json(body));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Received<SaveResponseRequest>().Count);
    }

    [TestMethod]
    public async Task Saved_Save_ForAThreadThatIsNotAllowed_Is403()
    {
        using var response = await _client.PostAsync("/saved", Json(
            """{"threadId":"blazor-session","label":"x","content":"y"}"""));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task Saved_Delete_Is204_Or404WhenThereWasNothing()
    {
        _agent.OnDeleteSaved = id => new DeleteSavedResponseAck { Success = id == "s1" };

        using var deleted = await _client.DeleteAsync("/saved/s1");
        using var missing = await _client.DeleteAsync("/saved/gone");

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        CollectionAssert.AreEqual(
            new[] { "s1", "gone" }, _agent.Received<DeleteSavedResponseRequest>().Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public async Task Saved_WhenTheAgentDoesNotAnswer_Is504()
    {
        await RestartAsync(Impatient);

        using var list = await _client.GetAsync("/saved");
        using var get = await _client.GetAsync("/saved/s1");
        using var save = await _client.PostAsync("/saved", Json("""{"label":"x","content":"y"}"""));
        using var delete = await _client.DeleteAsync("/saved/s1");

        await AssertProblemAsync(list, HttpStatusCode.GatewayTimeout);
        await AssertProblemAsync(get, HttpStatusCode.GatewayTimeout);
        await AssertProblemAsync(save, HttpStatusCode.GatewayTimeout);
        await AssertProblemAsync(delete, HttpStatusCode.GatewayTimeout);
    }

    // ── MCP sign-in ──────────────────────────────────────────────────────────

    private const string AuthorizationUri = "https://as.example/authorize?response_type=code&client_id=rockbot&state=st-123&code_challenge=abc";

    private void AgentStartsSignIns(string authorizationUri = AuthorizationUri) => _agent.OnSignInStart = request =>
        new McpSignInStartResponse
        {
            ServerName = request.ServerName, SignInId = "signin-1", AuthorizationUri = authorizationUri,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };

    private async Task<JsonElement> StartSignInAsync(string server = "scheduler")
    {
        using var response = await _client.PostAsync($"/mcp-signin/{server}/start", null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await BodyAsync(response);
    }

    [TestMethod]
    public async Task SignIn_List_ReturnsTheServers()
    {
        _agent.SignIns = new McpSignInListResponse
        {
            Servers =
            [
                new McpSignInServer { ServerName = "scheduler", State = McpSignInState.Required },
                new McpSignInServer { ServerName = "calendar", State = McpSignInState.SignedIn, Connected = true },
            ],
        };

        using var response = await _client.GetAsync("/mcp-signin");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var servers = (await BodyAsync(response)).GetProperty("servers");
        Assert.AreEqual("scheduler", servers[0].GetProperty("serverName").GetString());
        Assert.AreEqual("required", servers[0].GetProperty("state").GetString());
        Assert.IsFalse(servers[0].GetProperty("connected").GetBoolean());
        Assert.AreEqual("signed_in", servers[1].GetProperty("state").GetString());
        Assert.IsTrue(servers[1].GetProperty("connected").GetBoolean());
    }

    [TestMethod]
    public async Task SignIn_Start_ReturnsTheAuthorizationUrl_AndAsksForThisProxysCallback()
    {
        AgentStartsSignIns();

        var body = await StartSignInAsync();

        Assert.AreEqual("scheduler", body.GetProperty("serverName").GetString());
        Assert.AreEqual("signin-1", body.GetProperty("signInId").GetString());
        Assert.AreEqual(AuthorizationUri, body.GetProperty("authorizationUri").GetString());
        Assert.IsTrue(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        // No public address configured: the address of the request.
        Assert.AreEqual("http://localhost/mcp-oauth/callback", body.GetProperty("redirectUri").GetString());

        var request = _agent.Received<McpSignInStartRequest>().Single();
        Assert.AreEqual("scheduler", request.ServerName);
        Assert.AreEqual("http://localhost/mcp-oauth/callback", request.RedirectUri);
    }

    [TestMethod]
    public async Task SignIn_Start_BuildsTheRedirectUriFromThePublicAddress()
    {
        // Behind a TLS-terminating ingress the request is plain http to an internal host.
        await RestartAsync(("AgUi:PublicBaseUrl", "https://rockbot-agui.example.ts.net/"));
        AgentStartsSignIns();

        var body = await StartSignInAsync();

        Assert.AreEqual("https://rockbot-agui.example.ts.net/mcp-oauth/callback", body.GetProperty("redirectUri").GetString());
        Assert.AreEqual(
            "https://rockbot-agui.example.ts.net/mcp-oauth/callback",
            _agent.Received<McpSignInStartRequest>().Single().RedirectUri);
    }

    [TestMethod]
    public async Task SignIn_Start_TheClientCannotChooseTheRedirectUri()
    {
        AgentStartsSignIns();

        using var response = await _client.PostAsync(
            "/mcp-signin/scheduler/start?redirectUri=https%3A%2F%2Fevil.example%2Fcb",
            Json("""{"redirectUri":"https://evil.example/cb"}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("http://localhost/mcp-oauth/callback", _agent.Received<McpSignInStartRequest>().Single().RedirectUri);
    }

    [TestMethod]
    public async Task SignIn_Start_ThatTheAgentRefuses_Is422_WithItsReason()
    {
        _agent.OnSignInStart = request => new McpSignInStartResponse
        {
            ServerName = request.ServerName, Error = "No MCP server named \"nope\" uses OAuth sign-in.",
        };

        using var response = await _client.PostAsync("/mcp-signin/nope/start", null);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "nope");
    }

    [TestMethod]
    public async Task SignIn_Start_WithAnAuthorizationUrlThatHasNoState_Is502()
    {
        AgentStartsSignIns("https://as.example/authorize?response_type=code");

        using var response = await _client.PostAsync("/mcp-signin/scheduler/start", null);

        await AssertProblemAsync(response, HttpStatusCode.BadGateway, "state");
    }

    [TestMethod]
    public async Task SignIn_WhenTheAgentDoesNotAnswer_Is504()
    {
        await RestartAsync(Impatient);

        using var list = await _client.GetAsync("/mcp-signin");
        using var start = await _client.PostAsync("/mcp-signin/scheduler/start", null);

        await AssertProblemAsync(list, HttpStatusCode.GatewayTimeout);
        await AssertProblemAsync(start, HttpStatusCode.GatewayTimeout);
    }

    [TestMethod]
    public async Task Callback_RelaysTheRedirectOfASignInThisProxyStarted()
    {
        AgentStartsSignIns();
        await StartSignInAsync();

        using var response = await _client.GetAsync("/mcp-oauth/callback?code=abc&state=st-123&iss=https%3A%2F%2Fas.example");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "close this tab");

        var callback = await _agent.ReceivedAsync<McpSignInCallback>();
        Assert.AreEqual("st-123", callback.State);
        Assert.AreEqual("abc", callback.Code);
        Assert.AreEqual("https://as.example", callback.Iss);
        Assert.IsNull(callback.Error);
    }

    [TestMethod]
    public async Task Callback_WithAStateThisProxyDidNotIssue_Is400_AndNothingIsRelayed()
    {
        AgentStartsSignIns();
        await StartSignInAsync();

        using var response = await _client.GetAsync("/mcp-oauth/callback?code=abc&state=someone-elses");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Start the sign-in again");
        await SettleAsync();
        Assert.AreEqual(0, _agent.Received<McpSignInCallback>().Count);

        // The real redirect still goes through afterwards: a wrong guess doesn't spoil the sign-in.
        using var real = await _client.GetAsync("/mcp-oauth/callback?code=abc&state=st-123");
        Assert.AreEqual(HttpStatusCode.OK, real.StatusCode);
    }

    [TestMethod]
    public async Task Callback_WithNoSignInUnderWay_Is400_AndNothingIsRelayed()
    {
        using var response = await _client.GetAsync("/mcp-oauth/callback?code=abc&state=st-123");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Received<McpSignInCallback>().Count);
    }

    [TestMethod]
    public async Task Callback_WithoutAState_Is400_AndNothingIsRelayed()
    {
        AgentStartsSignIns();
        await StartSignInAsync();

        using var response = await _client.GetAsync("/mcp-oauth/callback?code=abc");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "incomplete");
        await SettleAsync();
        Assert.AreEqual(0, _agent.Received<McpSignInCallback>().Count);
    }

    [TestMethod]
    public async Task Callback_IsGoodOnce()
    {
        AgentStartsSignIns();
        await StartSignInAsync();

        using var first = await _client.GetAsync("/mcp-oauth/callback?code=abc&state=st-123");
        using var second = await _client.GetAsync("/mcp-oauth/callback?code=other&state=st-123");

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, second.StatusCode);
        await _agent.ReceivedAsync<McpSignInCallback>();
        await SettleAsync();
        Assert.AreEqual("abc", _agent.Received<McpSignInCallback>().Single().Code);
    }

    [TestMethod]
    public async Task Callback_WithoutACode_TellsTheAgent_SoTheSignInFailsAtOnce()
    {
        AgentStartsSignIns();
        await StartSignInAsync();

        using var response = await _client.GetAsync("/mcp-oauth/callback?state=st-123");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "without a code");
        var callback = await _agent.ReceivedAsync<McpSignInCallback>();
        Assert.AreEqual("st-123", callback.State);
        Assert.IsNull(callback.Code);
        Assert.IsNull(callback.Error);
    }

    [TestMethod]
    public async Task Callback_RelaysARefusal_AndShowsItsCodeEncoded()
    {
        AgentStartsSignIns();
        await StartSignInAsync();

        using var response = await _client.GetAsync(
            "/mcp-oauth/callback?state=st-123&error=" + Uri.EscapeDataString("<script>alert(1)</script>")
            + "&error_description=The+user+said+no");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(page, "refused");
        Assert.IsFalse(page.Contains("<script>", StringComparison.OrdinalIgnoreCase), page);

        var callback = await _agent.ReceivedAsync<McpSignInCallback>();
        Assert.AreEqual("<script>alert(1)</script>", callback.Error);
        Assert.AreEqual("The user said no", callback.ErrorDescription);
        Assert.IsNull(callback.Code);
    }

    [TestMethod]
    public async Task SignInStatus_ArrivesOnTheNotifyStream_WithTheSignInsId()
    {
        AgentStartsSignIns();
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();
        var started = await StartSignInAsync();

        await _agent.BroadcastAsync(new McpSignInStatus
        {
            ServerName = "scheduler", State = McpSignInState.Completed, SignInId = "signin-1",
        });

        var frame = await stream.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.McpSignInStatus, frame.Name);
        Assert.AreEqual(started.GetProperty("signInId").GetString(), frame.Value.GetProperty("signInId").GetString());
        Assert.AreEqual("completed", frame.Value.GetProperty("state").GetString());
    }

    // ── Clear context ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Clear_AsksTheAgent_AndTellsEveryClientOfTheThread_BeforeTheAgentConfirms()
    {
        // As the real agent does: it confirms with a reply nobody is waiting for.
        _agent.OnClear = (agent, request) => agent.BroadcastAsync(new AgentReply
        {
            Content = "Context cleared — starting fresh.", SessionId = request.SessionId,
            AgentName = AgUiTestHost.AgentName, IsFinal = true,
        });
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        using var response = await _client.PostAsync("/threads/agui-session/clear", null);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(Thread, (await _agent.ReceivedAsync<ClearContextRequest>()).SessionId);

        var frames = await stream.ReadAsync(4);
        Assert.AreEqual(TurnEventMapper.CustomNames.ContextCleared, frames[0].Name);
        Assert.AreEqual(Thread, frames[0].Value.GetProperty("threadId").GetString());
        // A client that empties its view on the event keeps the confirmation, which follows it.
        Assert.AreEqual("TEXT_MESSAGE_START", frames[1].Type);
        Assert.AreEqual("Context cleared — starting fresh.", Sse.Str(frames[2].Event, "delta"));
    }

    [TestMethod]
    public async Task Clear_IsReplayedInOrder_ToAClientThatReconnects()
    {
        string position;
        using (var stream = await NotifyStream.OpenAsync(_client))
        {
            position = (await stream.ReadConnectedAsync()).Id;
            await _agent.BroadcastAsync(new AgentReply { Content = "before", SessionId = Thread, AgentName = "RockBot", IsFinal = true });
            await stream.ReadAsync(4);
        }

        using var cleared = await _client.PostAsync("/threads/agui-session/clear", null);
        Assert.AreEqual(HttpStatusCode.Accepted, cleared.StatusCode);

        // What the reconnecting client missed comes in the order it happened: the clear is
        // after the message it clears.
        using var again = await NotifyStream.OpenAsync(_client, lastEventId: position);
        var replay = await again.ReadAsync(5);
        Assert.AreEqual("before", Sse.Str(replay[1].Event, "delta"));
        Assert.AreEqual(TurnEventMapper.CustomNames.ContextCleared, replay[4].Name);
    }

    [TestMethod]
    public async Task Clear_LeavesOpenQuestionsOpen()
    {
        await _agent.FormAsync(Question("q-1"));
        await QuestionsAsync(expected: 1);

        using var response = await _client.PostAsync("/threads/agui-session/clear", null);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        await _agent.ReceivedAsync<ClearContextRequest>();
        Assert.AreEqual(1, (await QuestionsAsync(expected: 1)).GetArrayLength(), "a question belongs to an MCP call, not to the conversation");
    }

    [TestMethod]
    public async Task Clear_ForAThreadThatIsNotAllowed_Is403_AndTheAgentIsNotAsked()
    {
        using var response = await _client.PostAsync("/threads/blazor-session/clear", null);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden);
        await SettleAsync();
        Assert.AreEqual(0, _agent.Received<ClearContextRequest>().Count);
    }

    // ── Open questions ───────────────────────────────────────────────────────

    private static McpQuestionForm Question(string questionId) => new()
    {
        QuestionId = questionId, ServerName = "scheduler", ToolName = "create_event", Message = "Which time zone?",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        Fields = [new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String, Title = "Time zone", Required = true }],
    };

    /// <summary>The thread's open questions, once there are <paramref name="expected"/> of them.</summary>
    private async Task<JsonElement> QuestionsAsync(int expected, string threadId = Thread)
    {
        var deadline = DateTime.UtcNow + NotifyStream.Patience;
        while (true)
        {
            using var response = await _client.GetAsync($"/threads/{threadId}/questions");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await BodyAsync(response);
            Assert.AreEqual(threadId, body.GetProperty("threadId").GetString());
            var questions = body.GetProperty("questions");
            if (questions.GetArrayLength() == expected)
                return questions;
            Assert.IsTrue(DateTime.UtcNow < deadline, $"expected {expected} open questions, found {questions.GetArrayLength()}");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task Questions_WithNoneOpen_IsAnEmptyList()
    {
        Assert.AreEqual(0, (await QuestionsAsync(expected: 0)).GetArrayLength());
    }

    [TestMethod]
    public async Task Questions_ListsTheOpenOnes_InTheShapeOfTheEvent()
    {
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();
        await _agent.FormAsync(Question("q-1"));
        await _agent.FormAsync(Question("q-2"));
        var events = await stream.ReadAsync(2);

        var questions = await QuestionsAsync(expected: 2);

        // The same JSON the rockbot.mcp_question event carried, oldest first.
        Assert.AreEqual(events[0].Value.GetRawText(), questions[0].GetRawText());
        Assert.AreEqual(events[1].Value.GetRawText(), questions[1].GetRawText());
        Assert.AreEqual("q-1", questions[0].GetProperty("form").GetProperty("questionId").GetString());
        Assert.AreEqual("q-1", questions[0].GetProperty("interrupt").GetProperty("id").GetString());
        Assert.AreEqual(Thread, questions[0].GetProperty("sessionId").GetString());
        StringAssert.Contains(questions[0].GetProperty("text").GetString(), "Which time zone?");
    }

    [TestMethod]
    public async Task Questions_ReachAClientThatConnectsAfterTheBufferHasMovedOn()
    {
        // The gap phase 3 left: no notify buffer to replay from, and the form has already gone by.
        await RestartAsync(("AgUi:NotifyBufferSize", "0"));
        await _agent.FormAsync(Question("q-1"));

        var questions = await QuestionsAsync(expected: 1);

        Assert.AreEqual("q-1", questions[0].GetProperty("form").GetProperty("questionId").GetString());
    }

    [TestMethod]
    public async Task Questions_DropASettledOne()
    {
        await _agent.FormAsync(Question("q-1"));
        await _agent.FormAsync(Question("q-2"));
        await QuestionsAsync(expected: 2);

        await _agent.StatusAsync("q-1", McpQuestionState.Answered, answeredBy: "user");

        var questions = await QuestionsAsync(expected: 1);
        Assert.AreEqual("q-2", questions[0].GetProperty("form").GetProperty("questionId").GetString());
    }

    [TestMethod]
    public async Task Questions_AreListedPerThread()
    {
        await RestartAsync(("AgUi:AllowedThreadIds:0", "one"), ("AgUi:AllowedThreadIds:1", "two"));
        await _agent.FormAsync(Question("q-1"), sessionId: "one");

        Assert.AreEqual(1, (await QuestionsAsync(expected: 1, threadId: "one")).GetArrayLength());
        Assert.AreEqual(0, (await QuestionsAsync(expected: 0, threadId: "two")).GetArrayLength());
    }

    [TestMethod]
    public async Task Questions_ForAThreadThatIsNotAllowed_Is403()
    {
        using var response = await _client.GetAsync("/threads/blazor-session/questions");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    // ── Agent info ───────────────────────────────────────────────────────────

    private void AgentReportsItself(bool processing = true)
    {
        _agent.Info = new AgentInfoResponse { AgentName = "Rocky", AgentVersion = "1.2.3" };
        _agent.OnStatus = _ => new ActiveStatusResponse
        {
            IsProcessing = processing,
            Subagents = [],
            Work =
            [
                new ActiveWorkItem { TaskId = "t1", Kind = ReplySourceKind.Subagent, Description = "Research", ThreadId = Thread, StartedAt = SavedAt },
                new ActiveWorkItem { TaskId = "t2", Kind = ReplySourceKind.Worker, Description = "Someone else's", ThreadId = "blazor-session", StartedAt = SavedAt },
            ],
        };
    }

    [TestMethod]
    public async Task Agent_ReturnsItsIdentity_AndWhatItIsDoingForTheThread()
    {
        AgentReportsItself();

        using var response = await _client.GetAsync("/agent?threadId=agui-session");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await BodyAsync(response);
        Assert.AreEqual("Rocky", body.GetProperty("agentName").GetString());
        Assert.AreEqual("1.2.3", body.GetProperty("agentVersion").GetString());
        Assert.AreEqual(Thread, body.GetProperty("threadId").GetString());
        Assert.IsTrue(body.GetProperty("isProcessing").GetBoolean());

        var work = body.GetProperty("work");
        Assert.AreEqual(1, work.GetArrayLength(), "another session's work is not this thread's business");
        Assert.AreEqual("t1", work[0].GetProperty("taskId").GetString());
        Assert.AreEqual("subagent", work[0].GetProperty("kind").GetString());

        // The agent is asked about this thread, so isProcessing is the thread's.
        Assert.AreEqual(Thread, _agent.Received<ActiveStatusRequest>().Single().SessionId);
    }

    [TestMethod]
    public async Task Agent_WithoutAThreadId_UsesTheOnlyThread()
    {
        AgentReportsItself(processing: false);

        using var response = await _client.GetAsync("/agent");

        var body = await BodyAsync(response);
        Assert.AreEqual(Thread, body.GetProperty("threadId").GetString());
        Assert.IsFalse(body.GetProperty("isProcessing").GetBoolean());
    }

    [TestMethod]
    public async Task Agent_ForAThreadThatIsNotAllowed_Is403()
    {
        AgentReportsItself();

        using var response = await _client.GetAsync("/agent?threadId=blazor-session");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task Agent_WhenTheAgentDoesNotAnswer_Is504()
    {
        await RestartAsync(Impatient);

        using var silent = await _client.GetAsync("/agent");
        // Half an answer is no answer: the status is part of what was asked for.
        _agent.Info = new AgentInfoResponse { AgentName = "Rocky", AgentVersion = "1.2.3" };
        using var half = await _client.GetAsync("/agent");

        await AssertProblemAsync(silent, HttpStatusCode.GatewayTimeout);
        await AssertProblemAsync(half, HttpStatusCode.GatewayTimeout);
    }

    // ── What stays reachable without a thread ────────────────────────────────

    [TestMethod]
    public async Task EveryNewEndpoint_RefusesAThreadThatIsNotAllowed()
    {
        // One place that lists them, so a new endpoint that takes a thread is easy to add here.
        (HttpMethod Method, string Url, HttpContent? Body)[] requests =
        [
            (HttpMethod.Post, "/attachments?threadId=nope&fileName=a.png", new ByteArrayContent(Png)),
            (HttpMethod.Post, "/feedback", Json("""{"threadId":"nope","messageId":"m","isPositive":true}""")),
            (HttpMethod.Post, "/saved", Json("""{"threadId":"nope","label":"x","content":"y"}""")),
            (HttpMethod.Post, "/threads/nope/clear", null),
            (HttpMethod.Get, "/threads/nope/questions", null),
            (HttpMethod.Get, "/agent?threadId=nope", null),
        ];

        foreach (var (method, url, body) in requests)
        {
            using var response = await _client.SendAsync(new HttpRequestMessage(method, url) { Content = body });
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {url}");
        }
    }
}
