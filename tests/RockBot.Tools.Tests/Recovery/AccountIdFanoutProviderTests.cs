using RockBot.Tools.Mcp.Recovery.Providers;

namespace RockBot.Tools.Tests.Recovery;

[TestClass]
public class AccountIdFanoutProviderTests
{
    [TestMethod]
    public void Extract_ArrayOfStrings()
    {
        var ids = AccountIdFanoutProvider.ExtractAccountIds("""["a@x.com","b@x.com"]""");
        CollectionAssert.AreEquivalent(new[] { "a@x.com", "b@x.com" }, ids);
    }

    [TestMethod]
    public void Extract_ArrayOfObjectsWithIdProperty()
    {
        var ids = AccountIdFanoutProvider.ExtractAccountIds("""[{"id":"a"},{"id":"b"}]""");
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, ids);
    }

    [TestMethod]
    public void Extract_ObjectWithAccountsArray()
    {
        var ids = AccountIdFanoutProvider.ExtractAccountIds("""{"accounts":[{"email":"a@x.com"},{"email":"b@x.com"}]}""");
        CollectionAssert.AreEquivalent(new[] { "a@x.com", "b@x.com" }, ids);
    }

    [TestMethod]
    public void Extract_DedupesAndSkipsEmpty()
    {
        var ids = AccountIdFanoutProvider.ExtractAccountIds("""["a","a","",null,"b"]""");
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, ids);
    }

    [TestMethod]
    public void Extract_NotJson_ReturnsEmpty()
    {
        var ids = AccountIdFanoutProvider.ExtractAccountIds("not json at all");
        Assert.AreEqual(0, ids.Count);
    }

    [TestMethod]
    public void Extract_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(0, AccountIdFanoutProvider.ExtractAccountIds(null).Count);
        Assert.AreEqual(0, AccountIdFanoutProvider.ExtractAccountIds("").Count);
    }

    [TestMethod]
    public void CanResolve_OnlyForCalendarMcpAccountId()
    {
        var p = new AccountIdFanoutProvider(
            (_, _, _) => Task.FromResult(new ToolInvokeResponse { ToolCallId = "x", ToolName = "y", Content = "[]" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountIdFanoutProvider>.Instance);

        Assert.IsTrue(p.CanResolve("calendar-mcp", "get_events", "accountId"));
        Assert.IsTrue(p.CanResolve("CALENDAR-MCP", "get_events", "ACCOUNTID"));
        Assert.IsFalse(p.CanResolve("calendar-mcp", "get_events", "timeZone"));
        Assert.IsFalse(p.CanResolve("other-server", "get", "accountId"));
    }
}
