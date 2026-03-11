using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using McpServer.Staging;

namespace McpServer.Staging.Tests;

[TestClass]
public class StagingApiTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _tempDir = null!;

    private const string TestToken = "test-staging-token";

    [TestInitialize]
    public void Initialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Staging__BasePath", _tempDir);
                b.UseSetting("Staging:Token", TestToken);
            });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-RockBot-Token", TestToken);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task StoreAndRead_RoundTrip_TextContent()
    {
        var content = "Hello, staging!";
        var put = await _client.PutAsync("/api/staging/test.txt", new StringContent(content));
        put.EnsureSuccessStatusCode();

        var get = await _client.GetAsync("/api/staging/test.txt");
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.AreEqual(content, await get.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public void Store_RejectsPathTraversal()
    {
        var repo = new StagingRepository(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Staging__BasePath"] = _tempDir })
            .Build());
        Assert.ThrowsExactly<ArgumentException>(() => repo.GetAbsolutePath("../evil.txt"));
    }

    [TestMethod]
    public async Task Get_Returns404_WhenNotFound()
    {
        var get = await _client.GetAsync("/api/staging/nonexistent.txt");
        Assert.AreEqual(HttpStatusCode.NotFound, get.StatusCode);
    }

    [TestMethod]
    public async Task Delete_Returns404_WhenNotFound()
    {
        var delete = await _client.DeleteAsync("/api/staging/nonexistent.txt");
        Assert.AreEqual(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [TestMethod]
    public async Task List_ReturnsStoredFiles()
    {
        await _client.PutAsync("/api/staging/drafts/report.md", new StringContent("# Report"));

        var list = await _client.GetAsync("/api/staging");
        Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
        var json = await list.Content.ReadAsStringAsync();
        Assert.IsTrue(json.Contains("drafts/report.md"));
    }

    [TestMethod]
    public void GetPath_ReturnsAbsolutePath()
    {
        var repo = new StagingRepository(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Staging__BasePath"] = _tempDir })
            .Build());
        var path = repo.GetAbsolutePath("drafts/report.xlsx");
        Assert.IsTrue(Path.IsPathRooted(path));
        Assert.IsTrue(path.Replace('\\', '/').Contains("drafts/report.xlsx"));
    }

    [TestMethod]
    public async Task Request_Without_Token_Returns_401()
    {
        var noAuth = _factory.CreateClient();
        var response = await noAuth.GetAsync("/api/staging/test.txt");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Request_With_Wrong_Token_Returns_401()
    {
        var wrongAuth = _factory.CreateClient();
        wrongAuth.DefaultRequestHeaders.Add("X-RockBot-Token", "wrong-token");
        var response = await wrongAuth.GetAsync("/api/staging/test.txt");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Health_Endpoint_Accessible_Without_Token()
    {
        var noAuth = _factory.CreateClient();
        var response = await noAuth.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
