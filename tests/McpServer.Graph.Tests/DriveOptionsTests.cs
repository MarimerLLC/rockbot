namespace McpServer.Graph.Tests;

/// <summary>
/// Compose always sets Graph__Drives__N__Alias for every slot it declares (see
/// docker-compose.yml), so an unconfigured slot binds as an empty string rather than being
/// absent. The server must still start and simply have no drive at that index.
/// </summary>
[TestClass]
public sealed class DriveOptionsTests : GraphServerTestBase
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings { get; } = new Dictionary<string, string?>
    {
        ["Graph:Drives:0:Alias"] = "",
        ["Graph:Drives:0:SiteId"] = "",
        ["Graph:Drives:1:Alias"] = "",
        ["Graph:Drives:1:UserPrincipalName"] = ""
    };

    [TestMethod]
    public async Task EmptyPlaceholderSlots_ServerStartsAndAliasIsReportedAsUnconfigured()
    {
        var result = await CallAsync("list_drive_items", new() { ["path"] = "/anything" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "none configured");
        Assert.AreEqual(0, Graph.Requests.Count);
    }
}
