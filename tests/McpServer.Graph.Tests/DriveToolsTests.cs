using System.Net;
using System.Text.Json;

namespace McpServer.Graph.Tests;

[TestClass]
public sealed class DriveToolsTests : GraphServerTestBase
{
    private const string DriveId = "b!driveId123";

    protected override IReadOnlyDictionary<string, string?> ExtraSettings { get; } = new Dictionary<string, string?>
    {
        ["Graph:Drives:0:Alias"] = "finance-ro",
        ["Graph:Drives:0:SiteId"] = "contoso.sharepoint.com,11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222",
        ["Graph:Drives:1:Alias"] = "jdoe-onedrive",
        ["Graph:Drives:1:UserPrincipalName"] = "jdoe@contoso.com"
    };

    private static HttpResponseMessage DriveIdResponse() => FakeGraphHandler.Json($$"""{"id":"{{DriveId}}"}""");

    [TestMethod]
    public async Task ListDriveItems_Root_ResolvesSiteDriveThenListsChildren()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive")
            ? DriveIdResponse()
            : FakeGraphHandler.Json("""{"value":[{"id":"1","name":"Budget.xlsx","size":100,"lastModifiedDateTime":"2026-10-12T09:30:00Z"}]}""");

        var items = await CallOkAsync("list_drive_items", new() { ["path"] = "/finance-ro" });

        Assert.AreEqual(2, Graph.Requests.Count);
        Assert.AreEqual("/v1.0/sites/contoso.sharepoint.com,11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222/drive",
            Uri.UnescapeDataString(Graph.Requests[0].RequestUri!.AbsolutePath));
        Assert.AreEqual($"/v1.0/drives/{DriveId}/root/children", Graph.Requests[1].RequestUri!.AbsolutePath);

        Assert.AreEqual(1, items.GetArrayLength());
        var item = items[0];
        Assert.AreEqual("Budget.xlsx", item.GetProperty("name").GetString());
        Assert.AreEqual("/finance-ro/Budget.xlsx", item.GetProperty("path").GetString());
        Assert.IsFalse(item.GetProperty("isFolder").GetBoolean());
    }

    [TestMethod]
    public async Task ListDriveItems_OneDriveAlias_ResolvesUsersDrive()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive") ? DriveIdResponse() : FakeGraphHandler.Json("""{"value":[]}""");

        await CallOkAsync("list_drive_items", new() { ["path"] = "/jdoe-onedrive" });

        Assert.AreEqual("/v1.0/users/jdoe%40contoso.com/drive", Graph.Requests[0].RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task ListDriveItems_SubFolder_UsesPathAddressingAndCachesDriveId()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive") ? DriveIdResponse() : FakeGraphHandler.Json("""{"value":[]}""");

        await CallOkAsync("list_drive_items", new() { ["path"] = "/finance-ro/Budgets/2026" });
        await CallOkAsync("list_drive_items", new() { ["path"] = "/finance-ro" });

        // Only the first call resolves the drive id; the second reuses the cached value.
        var resolutions = Graph.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/drive"));
        Assert.AreEqual(1, resolutions);
        StringAssert.EndsWith(Graph.Requests[1].RequestUri!.AbsolutePath, $"/drives/{DriveId}/root:/Budgets/2026:/children");
    }

    [TestMethod]
    public async Task GetDriveItem_Root_QueriesDriveRootDirectly()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive")
            ? DriveIdResponse()
            : FakeGraphHandler.Json("""{"id":"root","name":"Documents","folder":{"childCount":3}}""");

        var item = await CallOkAsync("get_drive_item", new() { ["path"] = "/finance-ro" });

        StringAssert.EndsWith(Graph.Requests[1].RequestUri!.AbsolutePath, $"/drives/{DriveId}/root");
        Assert.IsTrue(item.GetProperty("isFolder").GetBoolean());
    }

    [TestMethod]
    public async Task SearchDriveItems_DoublesEmbeddedQuotesAndEncodesTheLiteral()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive") ? DriveIdResponse() : FakeGraphHandler.Json("""{"value":[]}""");

        await CallOkAsync("search_drive_items", new() { ["path"] = "/finance-ro", ["query"] = "say 'hi'" });

        var path = Graph.Requests[1].RequestUri!.AbsolutePath;
        StringAssert.Contains(Uri.UnescapeDataString(path), "search(q='say ''hi''')");
    }

    [TestMethod]
    public async Task DownloadDriveFile_TextFile_ReturnsText()
    {
        Graph.Respond = req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/drive"))
                return DriveIdResponse();
            if (path.EndsWith(":/content"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello,world") };
            return FakeGraphHandler.Json("""{"name":"notes.csv","size":12,"file":{"mimeType":"text/csv"}}""");
        };

        var result = await CallOkAsync("download_drive_file", new() { ["path"] = "/finance-ro/notes.csv" });

        Assert.AreEqual("notes.csv", result.GetProperty("name").GetString());
        Assert.AreEqual("hello,world", result.GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task DownloadDriveFile_BinaryFile_ReturnsBase64Content()
    {
        var bytes = new byte[] { 0, 1, 2, 3 };
        Graph.Respond = req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/drive"))
                return DriveIdResponse();
            if (path.EndsWith(":/content"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return FakeGraphHandler.Json("""{"name":"photo.png","size":4,"file":{"mimeType":"image/png"}}""");
        };

        var result = await CallOkAsync("download_drive_file", new() { ["path"] = "/finance-ro/photo.png" });

        Assert.AreEqual(Convert.ToBase64String(bytes), result.GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task DownloadDriveFile_OverSizeLimit_IsErrorWithoutDownloadingContent()
    {
        var bigJson = """{"name":"big.zip","size":""" + (200 * 1024 * 1024) + ""","file":{"mimeType":"application/zip"}}""";
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive") ? DriveIdResponse() : FakeGraphHandler.Json(bigJson);

        var result = await CallAsync("download_drive_file", new() { ["path"] = "/finance-ro/big.zip" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "MB file limit");
        Assert.IsFalse(Graph.Requests.Any(r => r.RequestUri!.AbsolutePath.EndsWith(":/content")));
    }

    [TestMethod]
    public async Task UploadDriveFile_SetsConflictBehaviorFromOverwrite()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive")
            ? DriveIdResponse()
            : FakeGraphHandler.Json("""{"id":"1","name":"report.txt","size":5}""");

        await CallOkAsync("upload_drive_file",
            new() { ["path"] = "/finance-ro/report.txt", ["content_base64"] = Convert.ToBase64String("hello"u8.ToArray()), ["overwrite"] = true });

        var request = Graph.Requests[1];
        Assert.AreEqual(HttpMethod.Put, request.Method);
        StringAssert.EndsWith(request.RequestUri!.AbsolutePath, ":/content");
        StringAssert.Contains(Uri.UnescapeDataString(request.RequestUri.Query), "@microsoft.graph.conflictBehavior=replace");
    }

    [TestMethod]
    public async Task UploadDriveFile_InvalidBase64_IsErrorWithoutCallingGraph()
    {
        var result = await CallAsync("upload_drive_file", new() { ["path"] = "/finance-ro/report.txt", ["content_base64"] = "not-base64!" });

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task CreateDriveFolder_PostsToParentChildrenWithFailConflictBehavior()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive")
            ? DriveIdResponse()
            : FakeGraphHandler.Json("""{"id":"1","name":"2026","folder":{"childCount":0}}""");

        await CallOkAsync("create_drive_folder", new() { ["path"] = "/finance-ro/Budgets/2026" });

        var request = Graph.Requests[1];
        Assert.AreEqual(HttpMethod.Post, request.Method);
        StringAssert.EndsWith(request.RequestUri!.AbsolutePath, "/root:/Budgets:/children");
        var body = Graph.BodyOf(1);
        Assert.AreEqual("2026", body.GetProperty("name").GetString());
        Assert.AreEqual("fail", body.GetProperty("@microsoft.graph.conflictBehavior").GetString());
    }

    [TestMethod]
    public async Task DeleteDriveItem_SendsDelete()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive") ? DriveIdResponse() : new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await CallOkAsync("delete_drive_item", new() { ["path"] = "/finance-ro/old.txt" });

        Assert.AreEqual(HttpMethod.Delete, Graph.Requests[1].Method);
        Assert.AreEqual("/finance-ro/old.txt", result.GetProperty("deleted").GetString());
    }

    [TestMethod]
    [DataRow("/finance-ro/../other")]
    [DataRow("/finance-ro/Budgets/..")]
    public async Task ListDriveItems_PathTraversal_IsErrorWithoutCallingGraph(string path)
    {
        var result = await CallAsync("list_drive_items", new() { ["path"] = path });

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task ListDriveItems_UnknownAlias_IsErrorNamingConfiguredAliases()
    {
        var result = await CallAsync("list_drive_items", new() { ["path"] = "/not-configured" });

        Assert.IsTrue(result.IsError);
        var text = TextOf(result);
        StringAssert.Contains(text, "unknown drive alias");
        StringAssert.Contains(text, "finance-ro");
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task GraphForbidden_IsErrorPointingAtScopeDrivesScript()
    {
        Graph.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("/drive")
            ? DriveIdResponse()
            : FakeGraphHandler.Json("""{"error":{"code":"accessDenied","message":"Access is denied"}}""", HttpStatusCode.Forbidden);

        var result = await CallAsync("list_drive_items", new() { ["path"] = "/finance-ro" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "05-scope-drives.sh");
    }
}
