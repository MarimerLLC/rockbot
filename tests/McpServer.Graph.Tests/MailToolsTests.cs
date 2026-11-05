using System.Net;
using System.Text.Json;

namespace McpServer.Graph.Tests;

[TestClass]
public sealed class MailToolsTests : GraphServerTestBase
{
    private const string OneMessage = """
        {"value":[{"id":"AAMk-1_x=","subject":"Invoice","from":{"emailAddress":{"name":"Alice","address":"alice@example.com"}},
          "receivedDateTime":"2026-10-12T09:30:00Z","isRead":false,"hasAttachments":true,"bodyPreview":"Please pay"}]}
        """;

    [TestMethod]
    public async Task ListMessages_Defaults_QueriesInboxOfConfiguredMailboxWithBearer()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(OneMessage);

        var messages = await CallOkAsync("list_messages");

        var request = Graph.Single;
        Assert.AreEqual("/v1.0/users/me%40contoso.com/mailFolders/inbox/messages", request.RequestUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        StringAssert.Contains(query, "$top=10");
        StringAssert.Contains(query, "$orderby=receivedDateTime desc");
        Assert.IsFalse(query.Contains("$filter"));
        Assert.AreEqual("Bearer test-token", request.Headers.Authorization!.ToString());

        Assert.AreEqual(1, messages.GetArrayLength());
        var m = messages[0];
        Assert.AreEqual("AAMk-1_x=", m.GetProperty("id").GetString());
        Assert.AreEqual("Alice <alice@example.com>", m.GetProperty("from").GetString());
        Assert.IsFalse(m.GetProperty("isRead").GetBoolean());
        Assert.IsTrue(m.GetProperty("hasAttachments").GetBoolean());
        Assert.AreEqual("Please pay", m.GetProperty("preview").GetString());
        Assert.AreEqual("notFlagged", m.GetProperty("flagStatus").GetString());
    }

    [TestMethod]
    public async Task ListMessages_IncludesFlagStatusWhenPresent()
    {
        Graph.Respond = _ => FakeGraphHandler.Json("""
            {"value":[{"id":"AAMk-1_x=","subject":"Follow up","from":{"emailAddress":{"name":"Alice","address":"alice@example.com"}},
              "receivedDateTime":"2026-10-12T09:30:00Z","isRead":false,"hasAttachments":false,"bodyPreview":"...",
              "flag":{"flagStatus":"flagged"}}]}
            """);

        var messages = await CallOkAsync("list_messages");

        Assert.AreEqual("flagged", messages[0].GetProperty("flagStatus").GetString());
    }

    [TestMethod]
    public async Task ListMessages_UnreadOnly_FiltersOnIsRead()
    {
        await CallOkAsync("list_messages", new() { ["folder"] = "sentitems", ["top"] = 5, ["unread_only"] = true });

        var uri = Graph.Single.RequestUri!;
        StringAssert.EndsWith(uri.AbsolutePath, "/mailFolders/sentitems/messages");
        var query = Uri.UnescapeDataString(uri.Query);
        StringAssert.Contains(query, "$top=5");
        StringAssert.Contains(query, "isRead eq false");
    }

    [TestMethod]
    public async Task ListMessages_FlaggedOnly_FiltersOnFlagStatus()
    {
        await CallOkAsync("list_messages", new() { ["flagged_only"] = true });

        var query = Uri.UnescapeDataString(Graph.Single.RequestUri!.Query);
        StringAssert.Contains(query, "flag/flagStatus eq 'flagged'");
        Assert.IsFalse(query.Contains("isRead eq false"));
    }

    [TestMethod]
    public async Task ListMessages_UnreadAndFlaggedOnly_CombinesWithAnd()
    {
        await CallOkAsync("list_messages", new() { ["unread_only"] = true, ["flagged_only"] = true });

        var query = Uri.UnescapeDataString(Graph.Single.RequestUri!.Query);
        StringAssert.Contains(query, "isRead eq false and flag/flagStatus eq 'flagged'");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(26)]
    public async Task ListMessages_TopOutOfRange_IsErrorWithoutCallingGraph(int top)
    {
        var result = await CallAsync("list_messages", new() { ["top"] = top });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "top must be between 1 and 25");
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    [DataRow("../../users/ceo@contoso.com/messages")]
    [DataRow("inbox?$top=999")]
    [DataRow("")]
    public async Task ListMessages_UnsafeFolder_IsErrorWithoutCallingGraph(string folder)
    {
        var result = await CallAsync("list_messages", new() { ["folder"] = folder });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "invalid folder");
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task SearchMessages_QuotesQueryAndStripsEmbeddedQuotes()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(OneMessage);

        var messages = await CallOkAsync("search_messages", new() { ["query"] = "say \"hi\" invoice" });

        var uri = Graph.Single.RequestUri!;
        Assert.AreEqual("/v1.0/users/me%40contoso.com/messages", uri.AbsolutePath);
        StringAssert.Contains(Uri.UnescapeDataString(uri.Query), "$search=\"say hi invoice\"");
        Assert.AreEqual(1, messages.GetArrayLength());
    }

    [TestMethod]
    public async Task SearchMessages_EmptyQuery_IsError()
    {
        var result = await CallAsync("search_messages", new() { ["query"] = "  " });

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task GetMessage_RequestsTextBodyAndTruncatesLongBodies()
    {
        var body = new string('x', 1500);
        Graph.Respond = _ => FakeGraphHandler.Json(JsonSerializer.Serialize(new
        {
            id = "AAMk-1",
            subject = "Hello",
            from = new { emailAddress = new { name = "Bob", address = "bob@example.com" } },
            toRecipients = new[] { new { emailAddress = new { name = "me@contoso.com", address = "me@contoso.com" } } },
            ccRecipients = Array.Empty<object>(),
            receivedDateTime = "2026-10-12T09:30:00Z",
            isRead = true,
            hasAttachments = false,
            body = new { contentType = "text", content = body },
            flag = new { flagStatus = "complete" }
        }));

        var message = await CallOkAsync("get_message", new() { ["id"] = "AAMk-1" });

        var request = Graph.Single;
        Assert.AreEqual("/v1.0/users/me%40contoso.com/messages/AAMk-1", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("outlook.body-content-type=\"text\"", string.Join("", request.Headers.GetValues("Prefer")));

        Assert.AreEqual(1000, message.GetProperty("body").GetString()!.Length);
        Assert.IsTrue(message.GetProperty("bodyTruncated").GetBoolean());
        Assert.AreEqual("me@contoso.com", message.GetProperty("to")[0].GetString());
        Assert.AreEqual("Bob <bob@example.com>", message.GetProperty("from").GetString());
        Assert.AreEqual("complete", message.GetProperty("flagStatus").GetString());
    }

    [TestMethod]
    public async Task GetMessage_UnsafeId_IsErrorWithoutCallingGraph()
    {
        var result = await CallAsync("get_message", new() { ["id"] = "x/../../other" });

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task SetMessageFlag_Flags_SendsPatchWithFlagStatusFlagged()
    {
        Graph.Respond = _ => FakeGraphHandler.Json("""
            {"id":"AAMk-1","subject":"Invoice","from":{"emailAddress":{"name":"Alice","address":"alice@example.com"}},
              "receivedDateTime":"2026-10-12T09:30:00Z","isRead":false,"hasAttachments":false,"bodyPreview":"Please pay",
              "flag":{"flagStatus":"flagged"}}
            """);

        var message = await CallOkAsync("set_message_flag", new() { ["id"] = "AAMk-1", ["flagged"] = true });

        var request = Graph.Single;
        Assert.AreEqual(HttpMethod.Patch, request.Method);
        Assert.AreEqual("/v1.0/users/me%40contoso.com/messages/AAMk-1", request.RequestUri!.AbsolutePath);
        Assert.AreEqual("flagged", Graph.BodyOf(0).GetProperty("flag").GetProperty("flagStatus").GetString());
        Assert.AreEqual("flagged", message.GetProperty("flagStatus").GetString());
    }

    [TestMethod]
    public async Task SetMessageFlag_Unflags_SendsPatchWithFlagStatusNotFlagged()
    {
        Graph.Respond = _ => FakeGraphHandler.Json("""
            {"id":"AAMk-1","subject":"Invoice","from":{"emailAddress":{"name":"Alice","address":"alice@example.com"}},
              "receivedDateTime":"2026-10-12T09:30:00Z","isRead":false,"hasAttachments":false,"bodyPreview":"Please pay"}
            """);

        await CallOkAsync("set_message_flag", new() { ["id"] = "AAMk-1", ["flagged"] = false });

        Assert.AreEqual("notFlagged", Graph.BodyOf(0).GetProperty("flag").GetProperty("flagStatus").GetString());
    }

    [TestMethod]
    public async Task SetMessageFlag_UnsafeId_IsErrorWithoutCallingGraph()
    {
        var result = await CallAsync("set_message_flag", new() { ["id"] = "x/../../other", ["flagged"] = true });

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task GraphForbidden_IsErrorExplainingMailboxScoping()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(
            """{"error":{"code":"ErrorAccessDenied","message":"Access is denied"}}""", HttpStatusCode.Forbidden);

        var result = await CallAsync("list_messages");

        Assert.IsTrue(result.IsError);
        var text = TextOf(result);
        StringAssert.Contains(text, "403");
        StringAssert.Contains(text, "ErrorAccessDenied");
        StringAssert.Contains(text, "deploy/graph/setup.sh");
        Assert.IsFalse(text.Contains("test-token"));
    }

    [TestMethod]
    public async Task UnknownArgument_IsErrorWithoutCallingGraph()
    {
        var result = await CallAsync("list_messages", new() { ["mailbox"] = "ceo@contoso.com" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "'mailbox'");
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task ListTools_MailIsReadOnlyOnlyDeleteIsDestructiveAndAllSchemasAreStrict()
    {
        var tools = await (await ClientAsync()).ListToolsAsync();

        string[] readOnly =
        [
            "get_message", "list_messages", "search_messages", "get_attachment",
            "list_events", "get_event", "find_free_time",
            "list_contacts", "search_contacts", "get_contact",
            "list_drive_items", "get_drive_item", "search_drive_items", "download_drive_file"
        ];
        string[] writes =
        [
            "create_event", "update_event", "delete_event", "respond_to_event", "create_contact", "update_contact",
            "upload_drive_file", "create_drive_folder", "delete_drive_item", "set_message_flag"
        ];
        CollectionAssert.AreEquivalent(readOnly.Concat(writes).ToArray(), tools.Select(t => t.Name).ToArray());

        string[] destructive = ["delete_event", "delete_drive_item"];
        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            Assert.AreEqual(readOnly.Contains(tool.Name), annotations?.ReadOnlyHint ?? false, $"{tool.Name} read-only hint");
            if (!readOnly.Contains(tool.Name))
                Assert.AreEqual(destructive.Contains(tool.Name), annotations?.DestructiveHint ?? true, $"{tool.Name} destructive hint");
            Assert.IsFalse(tool.JsonSchema.GetProperty("additionalProperties").GetBoolean(), tool.Name);
        }
    }
}
