using System.Net;
using System.Text;
using System.Text.Json;

namespace McpServer.Graph.Tests;

[TestClass]
public sealed class AttachmentToolsTests : GraphServerTestBase
{
    private const string MessagePath = "/v1.0/users/me%40contoso.com/messages/AAMk-1";

    private static string Attachment(string type, string name, string? contentType, long size, bool inline = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@odata.type"] = $"#microsoft.graph.{type}",
            ["id"] = "att-1",
            ["name"] = name,
            ["contentType"] = contentType,
            ["size"] = size,
            ["isInline"] = inline
        });

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>Answers the metadata request with <paramref name="metadata"/> and $value with <paramref name="value"/>.</summary>
    private void Serve(string metadata, byte[]? value = null) =>
        Graph.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/$value")
            ? Bytes(value ?? [])
            : FakeGraphHandler.Json(metadata);

    [TestMethod]
    public async Task GetMessage_WithAttachments_ListsThem()
    {
        Graph.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/attachments")
            ? FakeGraphHandler.Json($$"""{"value":[{{Attachment("fileAttachment", "invoice.pdf", "application/pdf", 2048)}},{{Attachment("itemAttachment", "Fwd: hi", null, 900)}}]}""")
            : FakeGraphHandler.Json("""{"id":"AAMk-1","subject":"Invoice","hasAttachments":true,"body":{"content":"see attached"}}""");

        var message = await CallOkAsync("get_message", new() { ["id"] = "AAMk-1" });

        Assert.AreEqual(2, Graph.Requests.Count);
        Assert.AreEqual(MessagePath + "/attachments", Graph.Requests[1].RequestUri!.AbsolutePath);
        StringAssert.Contains(Uri.UnescapeDataString(Graph.Requests[1].RequestUri!.Query), "$select=id,name,contentType,size,isInline");
        var attachments = message.GetProperty("attachments");
        Assert.AreEqual("invoice.pdf", attachments[0].GetProperty("name").GetString());
        Assert.AreEqual("file", attachments[0].GetProperty("kind").GetString());
        Assert.AreEqual(2048, attachments[0].GetProperty("size").GetInt64());
        Assert.AreEqual("item", attachments[1].GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task GetMessage_WithoutAttachments_DoesNotListThem()
    {
        Graph.Respond = _ => FakeGraphHandler.Json("""{"id":"AAMk-1","hasAttachments":false,"body":{"content":"hi"}}""");

        var message = await CallOkAsync("get_message", new() { ["id"] = "AAMk-1" });

        Assert.AreEqual(1, Graph.Requests.Count);
        Assert.AreEqual(0, message.GetProperty("attachments").GetArrayLength());
    }

    [TestMethod]
    public async Task GetAttachment_BinaryFile_ReturnsBase64ContentForTheBridgeToSave()
    {
        var pdf = "%PDF-1.7\0binary"u8.ToArray();
        Serve(Attachment("fileAttachment", "invoice.pdf", "application/pdf", pdf.Length), pdf);

        var a = await CallOkAsync("get_attachment", new() { ["message_id"] = "AAMk-1", ["attachment_id"] = "att-1" });

        Assert.AreEqual(MessagePath + "/attachments/att-1/$value", Graph.Requests[1].RequestUri!.AbsolutePath);
        CollectionAssert.AreEqual(pdf, Convert.FromBase64String(a.GetProperty("content").GetString()!));
        Assert.AreEqual("invoice.pdf", a.GetProperty("name").GetString());
        Assert.AreEqual("application/pdf", a.GetProperty("contentType").GetString());
        Assert.IsFalse(a.TryGetProperty("text", out _));
    }

    [TestMethod]
    [DataRow("report.csv", "text/csv")]
    [DataRow("data.json", "application/octet-stream")]
    [DataRow("notes", "text/plain; charset=utf-8")]
    public async Task GetAttachment_TextFile_ReturnsText(string name, string contentType)
    {
        Serve(Attachment("fileAttachment", name, contentType, 11), "﻿a,b\n1,2 ✓"u8.ToArray());

        var a = await CallOkAsync("get_attachment", new() { ["message_id"] = "AAMk-1", ["attachment_id"] = "att-1" });

        Assert.AreEqual("a,b\n1,2 ✓", a.GetProperty("text").GetString());
        Assert.IsFalse(a.TryGetProperty("content", out _));
    }

    [TestMethod]
    public async Task GetAttachment_TextInDeclaredCharset_IsDecoded()
    {
        Serve(Attachment("fileAttachment", "old.txt", "text/plain; charset=windows-1252", 4), [0x63, 0x61, 0x66, 0xE9]);

        var a = await CallOkAsync("get_attachment", new() { ["message_id"] = "AAMk-1", ["attachment_id"] = "att-1" });

        Assert.AreEqual("café", a.GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task GetAttachment_LongText_IsTruncated()
    {
        Serve(Attachment("fileAttachment", "big.txt", "text/plain", 5000), Encoding.UTF8.GetBytes(new string('x', 5000)));

        var a = await CallOkAsync("get_attachment", new() { ["message_id"] = "AAMk-1", ["attachment_id"] = "att-1" });

        Assert.AreEqual(1000, a.GetProperty("text").GetString()!.Length);
        Assert.IsTrue(a.GetProperty("textTruncated").GetBoolean());
    }

    [TestMethod]
    public async Task GetAttachment_OverSizeLimit_IsErrorWithoutDownloading()
    {
        Serve(Attachment("fileAttachment", "huge.zip", "application/zip", 30L * 1_048_576));

        var result = await CallAsync("get_attachment", new() { ["message_id"] = "AAMk-1", ["attachment_id"] = "att-1" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "over the 25 MB attachment limit");
        Assert.AreEqual(1, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task GetAttachment_AttachedEmail_ReturnsTheMessage()
    {
        Graph.Respond = r => Uri.UnescapeDataString(r.RequestUri!.Query).Contains("$expand")
            ? FakeGraphHandler.Json("""
                {"id":"att-1","item":{"@odata.type":"#microsoft.graph.message","subject":"Original",
                 "from":{"emailAddress":{"name":"Carol","address":"carol@example.com"}},
                 "toRecipients":[{"emailAddress":{"address":"me@contoso.com"}}],
                 "receivedDateTime":"2026-10-01T01:00:00Z",
                 "body":{"contentType":"html","content":"<p>Hello&nbsp;there</p><p>Second</p>"}}}
                """)
            : FakeGraphHandler.Json(Attachment("itemAttachment", "Original", null, 900));

        var a = await CallOkAsync("get_attachment", new() { ["message_id"] = "AAMk-1", ["attachment_id"] = "att-1" });

        Assert.AreEqual("outlook.body-content-type=\"text\"", string.Join("", Graph.Requests[1].Headers.GetValues("Prefer")));
        var m = a.GetProperty("message");
        Assert.AreEqual("message", m.GetProperty("type").GetString());
        Assert.AreEqual("Original", m.GetProperty("subject").GetString());
        Assert.AreEqual("Carol <carol@example.com>", m.GetProperty("from").GetString());
        Assert.AreEqual("Hello there\nSecond", m.GetProperty("body").GetString());
        Assert.IsFalse(Graph.Requests.Any(r => r.RequestUri!.AbsolutePath.EndsWith("/$value")));
    }

    [TestMethod]
    public async Task GetAttachment_CloudLink_ExplainsItCannotBeDownloaded()
    {
        Serve(Attachment("referenceAttachment", "Plan.docx", null, 0));

        var a = await CallOkAsync("get_attachment", new() { ["message_id"] = "AAMk-1", ["attachment_id"] = "att-1" });

        Assert.AreEqual("reference", a.GetProperty("kind").GetString());
        StringAssert.Contains(a.GetProperty("note").GetString(), "OneDrive or SharePoint");
        Assert.AreEqual(1, Graph.Requests.Count);
    }

    [TestMethod]
    [DataRow("../other", "att-1")]
    [DataRow("AAMk-1", "att/../../x")]
    public async Task GetAttachment_UnsafeIds_AreErrorsWithoutCallingGraph(string messageId, string attachmentId)
    {
        var result = await CallAsync("get_attachment", new() { ["message_id"] = messageId, ["attachment_id"] = attachmentId });

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, Graph.Requests.Count);
    }
}
