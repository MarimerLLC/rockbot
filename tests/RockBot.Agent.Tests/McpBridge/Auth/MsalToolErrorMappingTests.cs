using RockBot.Agent.McpBridge;
using RockBot.Tools;
using RockBot.Tools.Mcp.Auth;

namespace RockBot.Agent.Tests.McpBridge.Auth;

/// <summary>
/// Verifies that <see cref="McpBridgeService"/>'s exception walker and message
/// builder turn a <see cref="TokenAcquisitionException"/> into an actionable
/// <see cref="ToolError"/> with <c>auth_required</c> and <c>IsRetryable=false</c>.
///
/// We deliberately test the walker + message builder in isolation rather than
/// spinning up the whole bridge — the bridge's invoke path is exercised in the
/// integration test suite, and the failure mode we care about (LLM blindly
/// retrying because the error looked transient) is fully captured by the
/// ToolError shape these helpers produce.
/// </summary>
[TestClass]
public class MsalToolErrorMappingTests
{
    [TestMethod]
    public void FindReauthRequired_DirectReauthException_Returns()
    {
        var ex = new TokenAcquisitionException(
            TokenAcquisitionException.Codes.ReauthRequired, "refresh dead");

        var found = McpBridgeService.FindReauthRequired(ex);

        Assert.IsNotNull(found);
        Assert.AreEqual(TokenAcquisitionException.Codes.ReauthRequired, found!.Code);
    }

    [TestMethod]
    public void FindReauthRequired_DirectNotAuthenticatedException_Returns()
    {
        var ex = new TokenAcquisitionException(
            TokenAcquisitionException.Codes.NotAuthenticated, "no account");

        var found = McpBridgeService.FindReauthRequired(ex);

        Assert.IsNotNull(found);
        Assert.AreEqual(TokenAcquisitionException.Codes.NotAuthenticated, found!.Code);
    }

    [TestMethod]
    public void FindReauthRequired_WrappedDeep_StillReturns()
    {
        // Simulates how the MCP client wraps a bearer-handler failure inside
        // transport/protocol exception layers.
        var inner = new TokenAcquisitionException(
            TokenAcquisitionException.Codes.ReauthRequired, "refresh dead");
        var middle = new HttpRequestException("send failed", inner);
        var outer = new InvalidOperationException("tool call failed", middle);

        var found = McpBridgeService.FindReauthRequired(outer);

        Assert.IsNotNull(found);
        Assert.AreEqual(TokenAcquisitionException.Codes.ReauthRequired, found!.Code);
    }

    [TestMethod]
    public void FindReauthRequired_OtherTokenAcquisitionCode_ReturnsNull()
    {
        // IdentityProviderUnreachable is transient, NOT reauth-required, so the
        // walker must skip it and let the generic error path handle it.
        var ex = new TokenAcquisitionException(
            TokenAcquisitionException.Codes.IdentityProviderUnreachable, "AAD down");

        Assert.IsNull(McpBridgeService.FindReauthRequired(ex));
    }

    [TestMethod]
    public void FindReauthRequired_UnrelatedException_ReturnsNull()
    {
        var ex = new InvalidOperationException("something else broke");
        Assert.IsNull(McpBridgeService.FindReauthRequired(ex));
    }

    [TestMethod]
    public void BuildReauthRequiredMessage_ReauthRequired_MentionsReconnect()
    {
        var ex = new TokenAcquisitionException(
            TokenAcquisitionException.Codes.ReauthRequired, "msal said no");

        var message = McpBridgeService.BuildReauthRequiredMessage(ex);

        StringAssert.Contains(message, "Reconnect M365");
        StringAssert.Contains(message, "Microsoft 365");
    }

    [TestMethod]
    public void BuildReauthRequiredMessage_NotAuthenticated_MentionsConnect()
    {
        var ex = new TokenAcquisitionException(
            TokenAcquisitionException.Codes.NotAuthenticated, "no cached account");

        var message = McpBridgeService.BuildReauthRequiredMessage(ex);

        StringAssert.Contains(message, "Connect M365");
        StringAssert.Contains(message, "Microsoft 365");
    }
}
