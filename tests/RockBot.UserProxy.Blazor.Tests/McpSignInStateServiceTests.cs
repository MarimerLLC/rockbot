using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

/// <summary>Which MCP servers the web chat prompts the user to sign in to (#738).</summary>
[TestClass]
public class McpSignInStateServiceTests
{
    private static McpSignInStatus Status(string server, string state, string? message = null) =>
        new() { ServerName = server, State = state, Message = message };

    [TestMethod]
    public void Required_ShowsAPrompt_AndCompletedClearsIt()
    {
        var sut = new McpSignInStateService();
        var changes = 0;
        sut.StateChanged += () => changes++;

        sut.Apply(Status("tasks", McpSignInState.Required));
        Assert.AreEqual("tasks", sut.NeedingSignIn.Single().ServerName);

        sut.Apply(Status("TASKS", McpSignInState.Completed));
        Assert.AreEqual(0, sut.NeedingSignIn.Count);
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void Failure_KeepsThePrompt_WithItsReason()
    {
        var sut = new McpSignInStateService();
        sut.Apply(Status("tasks", McpSignInState.Required));

        sut.Apply(Status("tasks", McpSignInState.Failed, "refused"));

        var entry = sut.NeedingSignIn.Single();
        Assert.AreEqual(McpSignInState.Failed, entry.State);
        Assert.AreEqual("refused", entry.Message);
    }

    [TestMethod]
    public void CancelledOrPending_LeavesThePromptAsItWas()
    {
        var sut = new McpSignInStateService();
        sut.Apply(Status("tasks", McpSignInState.Required));

        sut.Apply(Status("tasks", McpSignInState.Cancelled));
        sut.Apply(Status("tasks", McpSignInState.Pending));

        Assert.AreEqual(McpSignInState.Required, sut.NeedingSignIn.Single().State);
    }

    [TestMethod]
    public void Seed_AddsOnlyTheServersThatNeedASignIn()
    {
        var sut = new McpSignInStateService();
        Assert.IsTrue(sut.TryClaimSeed());
        Assert.IsFalse(sut.TryClaimSeed(), "one page seeds; the rest follow the broadcasts");

        sut.Seed(new McpSignInListResponse
        {
            Servers =
            [
                new McpSignInServer { ServerName = "a", State = McpSignInState.Required },
                new McpSignInServer { ServerName = "b", State = McpSignInState.SignedIn, Connected = true },
            ]
        });

        Assert.AreEqual("a", sut.NeedingSignIn.Single().ServerName);
    }

    [TestMethod]
    public async Task Frontend_FeedsTheState()
    {
        var sut = new McpSignInStateService();
        var frontend = new BlazorUserFrontend(new ChatStateService(), sut);

        await frontend.OnMcpSignInStatusAsync(Status("tasks", McpSignInState.Required));

        Assert.AreEqual(1, sut.NeedingSignIn.Count);
    }
}
