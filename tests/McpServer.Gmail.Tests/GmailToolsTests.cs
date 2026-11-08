using System.Net;
using System.Text;
using System.Text.Json;
using MimeKit;

namespace McpServer.Gmail.Tests;

[TestClass]
public sealed class GmailToolsTests : GmailServerTestBase
{
    private const string Id = "18c2f1a2b3c4d5e6";

    private static object H(string name, string value) => new { name, value };

    private static string Message(object payload, params string[] labels) => JsonSerializer.Serialize(new
    {
        id = Id, threadId = "18c2f1a2b3c4d000", labelIds = labels, snippet = "Hi &amp; welcome",
        internalDate = DateTimeOffset.Parse("2026-10-19T09:30:00+11:00").ToUnixTimeMilliseconds().ToString(),
        payload
    });

    private static readonly object MultipartPayload = new
    {
        partId = "", mimeType = "multipart/mixed",
        headers = new[] { H("From", "Alice <alice@example.com>"), H("To", "Simon <simon@gmail.com>, bob@example.com"), H("Subject", "Invoice") },
        body = new { size = 0 },
        parts = new object[]
        {
            new
            {
                partId = "0", mimeType = "multipart/alternative", filename = "", body = new { size = 0 },
                parts = new object[]
                {
                    new { partId = "0.0", mimeType = "text/plain", filename = "", headers = new[] { H("Content-Type", "text/plain; charset=UTF-8") }, body = new { size = 10, data = B64Url("Plain body ✓") } },
                    new { partId = "0.1", mimeType = "text/html", filename = "", body = new { size = 20, data = B64Url("<p>HTML body</p>") } }
                }
            },
            new { partId = "1", mimeType = "application/pdf", filename = "invoice.pdf",
                  headers = new[] { H("Content-Disposition", "attachment; filename=\"invoice.pdf\"") }, body = new { size = 2048, attachmentId = "ANGjdJ_long-id" } },
            new { partId = "2", mimeType = "text/csv", filename = "lines.csv",
                  headers = new[] { H("Content-Disposition", "attachment") }, body = new { size = 7, data = B64Url("a,b\n1,2") } },
            new { partId = "3", mimeType = "image/png", filename = "logo.png",
                  headers = new[] { H("Content-Disposition", "inline"), H("Content-ID", "<logo>") }, body = new { size = 99, attachmentId = "ANG-logo" } }
        }
    };

    [TestMethod]
    public async Task ListTools_ReadsAreReadOnly_DraftsAreTheOnlyWrites()
    {
        var tools = await (await ClientAsync()).ListToolsAsync();

        string[] reads = ["list_accounts", "search_messages", "list_messages", "get_message", "get_thread", "list_labels", "get_attachment"];
        string[] writes = ["create_draft", "update_draft"];
        CollectionAssert.AreEquivalent(reads.Concat(writes).ToArray(), tools.Select(t => t.Name).ToArray());
        foreach (var tool in tools)
        {
            Assert.AreEqual(reads.Contains(tool.Name), tool.ProtocolTool.Annotations?.ReadOnlyHint ?? false, tool.Name);
            if (writes.Contains(tool.Name))
                Assert.IsFalse(tool.ProtocolTool.Annotations?.DestructiveHint ?? true, tool.Name);
        }
    }

    [TestMethod]
    public async Task ListAccounts_NeedsNoGoogleCall()
    {
        var accounts = await CallOkAsync("list_accounts");

        Assert.AreEqual("personal", accounts[0].GetProperty("name").GetString());
        Assert.AreEqual(WorkEmail, accounts[1].GetProperty("email").GetString());
        Assert.AreEqual(0, Google.Requests.Count);
    }

    [TestMethod]
    public async Task Tokens_ArePerAccount_AndCachedBetweenCalls()
    {
        Google.Respond = _ => FakeGoogle.Json("""{"labels":[]}""");

        await CallOkAsync("list_labels", new() { ["account"] = "personal" });
        await CallOkAsync("list_labels", new() { ["account"] = "personal" });
        await CallOkAsync("list_labels", new() { ["account"] = "work" });

        CollectionAssert.AreEqual(new[] { "refresh-personal", "refresh-work" },
            Google.TokenCalls.Select(r => FakeGoogle.Form(r, "refresh_token")).ToArray());
        Assert.AreEqual("client-id", FakeGoogle.Form(Google.TokenCalls.First(), "client_id"));
        var bearers = Google.Gmail.Select(r => r.Bearer).ToArray();
        Assert.AreEqual(bearers[0], bearers[1]);
        StringAssert.StartsWith(bearers[2], "access-refresh-work");
        Assert.IsTrue(Google.Gmail.All(r => r.Path == "/gmail/v1/users/me/labels"));
    }

    [TestMethod]
    public async Task Unauthorized_RefreshesTheTokenAndRetriesOnce()
    {
        var calls = 0;
        Google.Respond = _ => calls++ == 0 ? FakeGoogle.Json("{}", HttpStatusCode.Unauthorized) : FakeGoogle.Json("""{"labels":[]}""");

        await CallOkAsync("list_labels", new() { ["account"] = "work" });

        Assert.AreEqual(2, Google.TokenCalls.Count());
        Assert.AreNotEqual(Google.Gmail.First().Bearer, Google.Gmail.Last().Bearer);
    }

    [TestMethod]
    public async Task InvalidGrant_ExplainsHowToReconnect()
    {
        Google.TokenResponse = _ => FakeGoogle.Json("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest);

        var error = await CallErrorAsync("list_labels", new() { ["account"] = "work" });

        StringAssert.Contains(error, "invalid_grant");
        StringAssert.Contains(error, "Testing mode");
        StringAssert.Contains(error, "deploy/gmail/setup.sh --account work");
        Assert.IsFalse(error.Contains("refresh-work"));
    }

    [TestMethod]
    public async Task UnknownAccount_IsErrorWithoutCallingGoogle()
    {
        StringAssert.Contains(await CallErrorAsync("list_labels", new() { ["account"] = "other" }), "configured accounts: personal, work");
        Assert.AreEqual(0, Google.Requests.Count);
    }

    [TestMethod]
    public async Task SearchMessages_PassesQuery_AndFetchesMetadataForEachHit()
    {
        Google.Respond = r => r.Path.EndsWith("/messages")
            ? FakeGoogle.Json($$"""{"messages":[{"id":"{{Id}}","threadId":"t"}],"nextPageToken":"page-2","resultSizeEstimate":42}""")
            : FakeGoogle.Json(Message(new { headers = new[] { H("From", "Alice <alice@example.com>"), H("Subject", "Invoice") } }, "INBOX", "UNREAD"));

        var page = await CallOkAsync("search_messages", new() { ["account"] = "personal", ["query"] = "from:alice is:unread", ["top"] = 5 });

        var list = Google.Gmail.First();
        StringAssert.Contains(list.Query, "q=from:alice is:unread");
        StringAssert.Contains(list.Query, "maxResults=5");
        StringAssert.Contains(Google.Gmail.Last().Query, "format=metadata");
        var m = page.GetProperty("messages")[0];
        Assert.AreEqual("Alice <alice@example.com>", m.GetProperty("from").GetString());
        Assert.AreEqual("Hi & welcome", m.GetProperty("snippet").GetString());
        Assert.IsTrue(m.GetProperty("isUnread").GetBoolean());
        Assert.AreEqual("2026-10-19T09:30:00+11:00", m.GetProperty("date").GetString());
        Assert.AreEqual("page-2", page.GetProperty("nextPageToken").GetString());
        Assert.AreEqual(42, page.GetProperty("resultSizeEstimate").GetInt32());
    }

    [TestMethod]
    public async Task ListMessages_UnreadOnly_FiltersLabelAndUnread()
    {
        Google.Respond = _ => FakeGoogle.Json("{}");

        await CallOkAsync("list_messages", new() { ["account"] = "work", ["label"] = "STARRED", ["unread_only"] = true });

        var query = Google.Gmail.Single().Query;
        StringAssert.Contains(query, "labelIds=STARRED");
        StringAssert.Contains(query, "q=is:unread");
    }

    [TestMethod]
    public async Task GetMessage_PrefersPlainText_AndListsAttachmentsByPartId()
    {
        Google.Respond = _ => FakeGoogle.Json(Message(MultipartPayload, "INBOX"));

        var m = await CallOkAsync("get_message", new() { ["account"] = "personal", ["id"] = Id });

        StringAssert.Contains(Google.Gmail.Single().Query, "format=full");
        Assert.AreEqual("Plain body ✓", m.GetProperty("body").GetString());
        CollectionAssert.AreEqual(new[] { "Simon <simon@gmail.com>", "bob@example.com" },
            m.GetProperty("to").EnumerateArray().Select(e => e.GetString()).ToArray());
        var attachments = m.GetProperty("attachments");
        Assert.AreEqual(3, attachments.GetArrayLength());
        Assert.AreEqual("1", attachments[0].GetProperty("id").GetString());
        Assert.AreEqual("invoice.pdf", attachments[0].GetProperty("name").GetString());
        Assert.AreEqual(2048, attachments[0].GetProperty("size").GetInt64());
        Assert.IsFalse(attachments[0].GetProperty("isInline").GetBoolean());
        Assert.IsTrue(attachments[2].GetProperty("isInline").GetBoolean());
    }

    [TestMethod]
    public async Task GetMessage_HtmlOnly_IsConvertedToText()
    {
        Google.Respond = _ => FakeGoogle.Json(Message(new
        {
            partId = "", mimeType = "text/html", filename = "",
            body = new { size = 50, data = B64Url("<html><head><style>p{}</style></head><body><p>Hello&nbsp;there</p><div>Line two</div></body></html>") }
        }));

        var m = await CallOkAsync("get_message", new() { ["account"] = "personal", ["id"] = Id });

        Assert.AreEqual("Hello there\nLine two", m.GetProperty("body").GetString());
    }

    [TestMethod]
    public async Task GetAttachment_Binary_DownloadsByFreshAttachmentIdAndReturnsBase64()
    {
        var pdf = "%PDF-1.7\0x"u8.ToArray();
        Google.Respond = r => r.Path.Contains("/attachments/")
            ? FakeGoogle.Json(JsonSerializer.Serialize(new { size = pdf.Length, data = B64Url(pdf) }))
            : FakeGoogle.Json(Message(MultipartPayload));

        var a = await CallOkAsync("get_attachment", new() { ["account"] = "personal", ["message_id"] = Id, ["attachment_id"] = "1" });

        Assert.AreEqual($"/gmail/v1/users/me/messages/{Id}/attachments/ANGjdJ_long-id", Google.Gmail.Last().Path);
        CollectionAssert.AreEqual(pdf, Convert.FromBase64String(a.GetProperty("content").GetString()!));
        Assert.AreEqual("invoice.pdf", a.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task GetAttachment_SmallTextPart_IsReturnedAsTextWithoutASecondDownload()
    {
        Google.Respond = _ => FakeGoogle.Json(Message(MultipartPayload));

        var a = await CallOkAsync("get_attachment", new() { ["account"] = "personal", ["message_id"] = Id, ["attachment_id"] = "2" });

        Assert.AreEqual("a,b\n1,2", a.GetProperty("text").GetString());
        Assert.AreEqual(1, Google.Gmail.Count());
    }

    [TestMethod]
    [DataRow("9", "no attachment with id 9")]
    [DataRow("../x", "invalid attachment_id")]
    public async Task GetAttachment_BadId_IsError(string attachmentId, string message)
    {
        Google.Respond = _ => FakeGoogle.Json(Message(MultipartPayload));

        StringAssert.Contains(await CallErrorAsync("get_attachment", new() { ["account"] = "personal", ["message_id"] = Id, ["attachment_id"] = attachmentId }), message);
    }

    [TestMethod]
    public async Task CreateDraft_Reply_ThreadsAndAddressesIt()
    {
        Google.Respond = r => r.Method == HttpMethod.Post
            ? FakeGoogle.Json("""{"id":"r-123","message":{"id":"18c2ffff00000001","threadId":"18c2f1a2b3c4d000"}}""")
            : FakeGoogle.Json(JsonSerializer.Serialize(new
            {
                id = Id, threadId = "18c2f1a2b3c4d000",
                payload = new
                {
                    headers = new[]
                    {
                        H("Message-ID", "<orig@mail.example.com>"), H("References", "<first@mail.example.com>"),
                        H("Subject", "Invoice"), H("From", "Alice <alice@example.com>"),
                        H("To", "simon@gmail.com, Bob <bob@example.com>"), H("Cc", "carol@example.com, SIMON@gmail.com")
                    }
                }
            }));

        var draft = await CallOkAsync("create_draft", new()
        {
            ["account"] = "personal", ["reply_to_message_id"] = Id, ["body"] = "Paid, thanks.", ["reply_all"] = true
        });

        var post = Google.Gmail.Last();
        Assert.AreEqual("/gmail/v1/users/me/drafts", post.Path);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.AreEqual("18c2f1a2b3c4d000", body.RootElement.GetProperty("message").GetProperty("threadId").GetString());
        var raw = body.RootElement.GetProperty("message").GetProperty("raw").GetString()!;
        var mime = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/').PadRight(raw.Length + (4 - raw.Length % 4) % 4, '='))));
        Assert.AreEqual(PersonalEmail, mime.From.Mailboxes.Single().Address);
        Assert.AreEqual("alice@example.com", mime.To.Mailboxes.Single().Address);
        CollectionAssert.AreEquivalent(new[] { "bob@example.com", "carol@example.com" }, mime.Cc.Mailboxes.Select(m => m.Address).ToArray());
        Assert.AreEqual("Re: Invoice", mime.Subject);
        Assert.AreEqual("orig@mail.example.com", mime.InReplyTo);
        CollectionAssert.AreEqual(new[] { "first@mail.example.com", "orig@mail.example.com" }, mime.References.ToArray());
        Assert.AreEqual("Paid, thanks.", mime.TextBody?.Trim());

        Assert.AreEqual("r-123", draft.GetProperty("draftId").GetString());
        StringAssert.Contains(draft.GetProperty("link").GetString(), "#drafts?compose=18c2ffff00000001");
        StringAssert.Contains(draft.GetProperty("note").GetString(), "has not been sent");
        Assert.IsFalse(Google.Requests.Any(r => r.Path.EndsWith("/send")));
    }

    [TestMethod]
    public async Task CreateDraft_New_UsesTheAccountAsSender()
    {
        Google.Respond = _ => FakeGoogle.Json("""{"id":"r-9","message":{"id":"18c2ffff00000002"}}""");

        await CallOkAsync("create_draft", new()
        {
            ["account"] = "work", ["to"] = new[] { "Dana <dana@example.com>" }, ["subject"] = "Hello", ["body"] = "Hi Dana"
        });

        using var body = JsonDocument.Parse(Google.Gmail.Single().Body!);
        var raw = body.RootElement.GetProperty("message").GetProperty("raw").GetString()!;
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/').PadRight(raw.Length + (4 - raw.Length % 4) % 4, '=')));
        StringAssert.Contains(text, $"From: {WorkEmail}");
        StringAssert.Contains(text, "To: Dana <dana@example.com>");
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("message").GetProperty("threadId").ValueKind);
    }

    [TestMethod]
    [DataRow(null, "give at least one recipient")]
    [DataRow("not-an-address", "not a valid email address")]
    public async Task CreateDraft_BadRecipients_AreErrors(string? to, string message)
    {
        var args = new Dictionary<string, object?> { ["account"] = "work", ["body"] = "x" };
        if (to is not null)
            args["to"] = new[] { to };

        StringAssert.Contains(await CallErrorAsync("create_draft", args), message);
        Assert.IsFalse(Google.Gmail.Any(r => r.Method == HttpMethod.Post));
    }

    [TestMethod]
    public async Task UpdateDraft_ChangesOnlyTheGivenFields()
    {
        var existing = new MimeMessage();
        existing.From.Add(MailboxAddress.Parse(WorkEmail));
        existing.To.Add(MailboxAddress.Parse("dana@example.com"));
        existing.Subject = "Old subject";
        existing.Body = new TextPart("plain") { Text = "Keep this body" };
        using var stream = new MemoryStream();
        existing.WriteTo(stream);

        Google.Respond = r => r.Method == HttpMethod.Get
            ? FakeGoogle.Json(JsonSerializer.Serialize(new { id = "r-9", message = new { id = "18c2ffff00000002", threadId = "18c2f1a2b3c4d000", raw = B64Url(stream.ToArray()) } }))
            : FakeGoogle.Json("""{"id":"r-9","message":{"id":"18c2ffff00000003","threadId":"18c2f1a2b3c4d000"}}""");

        await CallOkAsync("update_draft", new() { ["account"] = "work", ["draft_id"] = "r-9", ["subject"] = "New subject" });

        var put = Google.Gmail.Last();
        Assert.AreEqual(HttpMethod.Put, put.Method);
        Assert.AreEqual("/gmail/v1/users/me/drafts/r-9", put.Path);
        using var body = JsonDocument.Parse(put.Body!);
        var raw = body.RootElement.GetProperty("message").GetProperty("raw").GetString()!;
        var mime = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/').PadRight(raw.Length + (4 - raw.Length % 4) % 4, '='))));
        Assert.AreEqual("New subject", mime.Subject);
        Assert.AreEqual("dana@example.com", mime.To.Mailboxes.Single().Address);
        Assert.AreEqual("Keep this body", mime.TextBody?.Trim());
        Assert.AreEqual("18c2f1a2b3c4d000", body.RootElement.GetProperty("message").GetProperty("threadId").GetString());
    }

    [TestMethod]
    public async Task UpdateDraft_NothingGiven_IsError()
    {
        StringAssert.Contains(await CallErrorAsync("update_draft", new() { ["account"] = "work", ["draft_id"] = "r-9" }), "nothing to update");
    }

    [TestMethod]
    [DataRow("get_message", "id", "../../profile")]
    [DataRow("get_thread", "thread_id", "x y")]
    public async Task UnsafeIds_AreErrorsWithoutCallingGoogle(string tool, string arg, string value)
    {
        StringAssert.Contains(await CallErrorAsync(tool, new() { ["account"] = "work", [arg] = value }), "invalid");
        Assert.AreEqual(0, Google.Requests.Count);
    }
}
