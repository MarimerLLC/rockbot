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
    public void RenderAnchor_WithZone_ShowsTheStartInThatZone()
    {
        // 09:46 UTC is 04:46 in Chicago (CDT, UTC-5, in June) whatever this machine's own zone is.
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var anchor = ReplyOriginFormatter.RenderAnchor(Origin("scheduled", "sched-1"), "blazor", "s1", Now, chicago);
        Assert.AreEqual("↳ Re: \"deep research 8 topics\"\n   started 04:46 from scheduled · 2h 14m ago", anchor);
        Assert.AreEqual("May 1", ReplyOriginFormatter.RelativeTime(new DateTimeOffset(2026, 5, 2, 3, 0, 0, TimeSpan.Zero), Now, chicago));
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

    // ── #841: the viewing-session overload ───────────────────────────────────

    [TestMethod]
    [DataRow(ReplySourceKind.Subagent, "abcdef1234567890", "↳ From a subagent (task abcdef12) in another conversation")]
    [DataRow(ReplySourceKind.Worker, "w1", "↳ From a worker (task w1) in another conversation")]
    [DataRow(ReplySourceKind.Scheduled, null, "↳ From a scheduled task in another conversation")]
    [DataRow(ReplySourceKind.A2A, null, "↳ From an A2A agent in another conversation")]
    [DataRow(ReplySourceKind.Primary, null, "↳ From the agent in another conversation")]
    [DataRow(null, null, "↳ From the agent in another conversation")]
    public void RenderAnchor_NoOrigin_OtherSession_FallsBackPerSource(string? kind, string? taskId, string firstLine)
    {
        var source = kind is null ? null : new ReplySource { Kind = kind, TaskId = taskId };

        var anchor = ReplyOriginFormatter.RenderAnchor(null, source, "cli-session", "blazor", "blazor-session", Now);

        Assert.AreEqual(firstLine + "\n   session \"cli-session\"", anchor);
    }

    [TestMethod]
    public void RenderAnchor_NoOrigin_ViewedSession_ReturnsNull()
    {
        Assert.IsNull(ReplyOriginFormatter.RenderAnchor(null, ReplySource.Subagent("t1"), "s1", "cli", "s1", Now));
        Assert.IsNull(ReplyOriginFormatter.RenderAnchor(null, ReplySource.Subagent("t1"), "session/s1", "cli", "s1", Now));
    }

    [TestMethod]
    public void RenderAnchor_NoOrigin_NoViewingSession_MeansTheRepliesOwn()
    {
        Assert.IsNull(ReplyOriginFormatter.RenderAnchor(null, ReplySource.Scheduled, "scheduled", "cli", null, Now));
    }

    [TestMethod]
    public void RenderAnchor_WithOrigin_UsesOriginAnchor_SuppressedOnlyInViewedSession()
    {
        var shown = ReplyOriginFormatter.RenderAnchor(Origin("cli", "one-shot"), null, "one-shot", "cli", "cli-session", Now);
        var suppressed = ReplyOriginFormatter.RenderAnchor(Origin("cli", "cli-session"), null, "cli-session", "cli", "cli-session", Now);

        Assert.AreEqual(ReplyOriginFormatter.RenderAnchor(Origin("cli", "one-shot"), "cli", "cli-session", Now), shown);
        Assert.IsNull(suppressed);
    }

    [TestMethod]
    public void WithQuotedAnchor_Fallback_IsQuoted()
    {
        var content = ReplyOriginFormatter.WithQuotedAnchor(
            "the result", null, ReplySource.Scheduled, "scheduled", "blazor", "blazor-session", Now);

        Assert.AreEqual("> ↳ From a scheduled task in another conversation\n> session \"scheduled\"\n\nthe result", content);
    }

    [TestMethod]
    public void ResolveChannelName_PrefersExplicit_ThenProxyIdPrefix()
    {
        Assert.AreEqual("discord", new UserProxyOptions { ChannelName = "discord", ProxyId = "x-y" }.ResolveChannelName());
        Assert.AreEqual("cli", new UserProxyOptions { ProxyId = "cli-rocky-abc" }.ResolveChannelName());
        Assert.AreEqual("blazor", new UserProxyOptions { ProxyId = "blazor" }.ResolveChannelName());
    }
}
