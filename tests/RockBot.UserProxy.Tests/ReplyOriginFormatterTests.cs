namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class ReplyOriginFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static ReplyOrigin Origin(string channel = "cli", string? session = "s1", DateTimeOffset? at = null) =>
        new(channel, "deep research 8 topics", at ?? Now.AddMinutes(-134), session);

    [TestMethod]
    public void RelativeTime_Buckets()
    {
        Assert.AreEqual("just now", ReplyOriginFormatter.RelativeTime(Now.AddSeconds(-5), Now));
        Assert.AreEqual("3m ago", ReplyOriginFormatter.RelativeTime(Now.AddMinutes(-3), Now));
        Assert.AreEqual("2h 14m ago", ReplyOriginFormatter.RelativeTime(Now.AddMinutes(-134), Now));
        Assert.AreEqual("5h ago", ReplyOriginFormatter.RelativeTime(Now.AddHours(-5), Now));
        Assert.AreEqual("yesterday", ReplyOriginFormatter.RelativeTime(Now.AddHours(-30), Now));
        Assert.AreEqual("3d ago", ReplyOriginFormatter.RelativeTime(Now.AddDays(-3), Now));
    }

    [TestMethod]
    public void RelativeTime_FutureClampsToJustNow()
    {
        Assert.AreEqual("just now", ReplyOriginFormatter.RelativeTime(Now.AddMinutes(5), Now));
    }

    [TestMethod]
    public void RenderAnchor_NullOrigin_ReturnsNull()
    {
        Assert.IsNull(ReplyOriginFormatter.RenderAnchor(null, "cli", "s1", Now));
    }

    [TestMethod]
    public void RenderAnchor_SameChannelAndSession_Suppressed()
    {
        var anchor = ReplyOriginFormatter.RenderAnchor(Origin("blazor", "s1"), "blazor", "s1", Now);
        Assert.IsNull(anchor);
    }

    [TestMethod]
    public void RenderAnchor_DifferentChannel_Shows()
    {
        var anchor = ReplyOriginFormatter.RenderAnchor(Origin("cli", "s1"), "blazor", "s1", Now);
        Assert.IsNotNull(anchor);
        StringAssert.Contains(anchor, "deep research 8 topics");
        StringAssert.Contains(anchor, "from cli");
        StringAssert.Contains(anchor, "2h 14m ago");
    }

    [TestMethod]
    public void RenderAnchor_SameChannelDifferentSession_Shows()
    {
        var anchor = ReplyOriginFormatter.RenderAnchor(Origin("blazor", "other"), "blazor", "s1", Now);
        Assert.IsNotNull(anchor);
    }

    [TestMethod]
    public void RenderAnchor_NoCurrentContext_Shows()
    {
        var anchor = ReplyOriginFormatter.RenderAnchor(Origin("scheduled", "sched-1"), null, null, Now);
        Assert.IsNotNull(anchor);
    }

    [TestMethod]
    public void ResolveChannelName_PrefersExplicit_ThenProxyIdPrefix()
    {
        Assert.AreEqual("discord", new UserProxyOptions { ChannelName = "discord", ProxyId = "x-y" }.ResolveChannelName());
        Assert.AreEqual("cli", new UserProxyOptions { ProxyId = "cli-rocky-abc" }.ResolveChannelName());
        Assert.AreEqual("blazor", new UserProxyOptions { ProxyId = "blazor" }.ResolveChannelName());
    }
}
