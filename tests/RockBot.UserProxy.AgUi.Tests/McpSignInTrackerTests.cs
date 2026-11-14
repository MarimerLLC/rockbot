namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// Which sign-in redirects the proxy relays: only those of sign-ins it started, each once, and
/// only while the sign-in can still finish. Also the public address the redirect URI is built from.
/// </summary>
[TestClass]
public sealed class McpSignInTrackerTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    private static McpSignInStartResponse Started(string state, string server = "scheduler", DateTimeOffset? expiresAt = null) => new()
    {
        ServerName = server, SignInId = $"signin-{state}", ExpiresAt = expiresAt,
        AuthorizationUri = $"https://as.example/authorize?client_id=rockbot&state={Uri.EscapeDataString(state)}&code_challenge=x",
    };

    [TestMethod]
    public void Take_ReturnsTheSignInAStateBelongsTo_Once()
    {
        var tracker = new McpSignInTracker(new Clock(Start));
        Assert.IsTrue(tracker.Track(Started("st-1")));

        var first = tracker.Take("st-1");
        var second = tracker.Take("st-1");

        Assert.AreEqual("scheduler", first?.ServerName);
        Assert.AreEqual("signin-st-1", first?.SignInId);
        Assert.IsNull(second, "a state is good for one redirect");
    }

    [TestMethod]
    [DataRow("st-2")]
    [DataRow("ST-1", DisplayName = "a state is matched exactly")]
    [DataRow("")]
    [DataRow(null)]
    public void Take_OfAStateThatWasNeverIssued_IsNull_AndLeavesTheRealOne(string? state)
    {
        var tracker = new McpSignInTracker(new Clock(Start));
        tracker.Track(Started("st-1"));

        Assert.IsNull(tracker.Take(state));
        Assert.IsNotNull(tracker.Take("st-1"));
    }

    [TestMethod]
    public void Track_ReadsAnEncodedState()
    {
        var tracker = new McpSignInTracker(new Clock(Start));
        tracker.Track(Started("a b+c/d="));

        Assert.IsNotNull(tracker.Take("a b+c/d="));
    }

    [TestMethod]
    [DataRow("https://as.example/authorize?client_id=rockbot")]
    [DataRow("https://as.example/authorize?state=")]
    [DataRow("not a url")]
    [DataRow(null)]
    public void Track_WithoutAState_IsRefused(string? authorizationUri)
    {
        var tracker = new McpSignInTracker(new Clock(Start));

        Assert.IsFalse(tracker.Track(new McpSignInStartResponse { ServerName = "scheduler", AuthorizationUri = authorizationUri }));
    }

    [TestMethod]
    public void ASignIn_IsForgottenWhenItExpires()
    {
        var clock = new Clock(Start);
        var tracker = new McpSignInTracker(clock);
        tracker.Track(Started("early", expiresAt: Start.AddMinutes(5)));
        tracker.Track(Started("late", expiresAt: Start.AddMinutes(30)));
        tracker.Track(Started("unsaid"));

        // A redirect already on its way when the sign-in expires gets a minute's grace, no more.
        clock.Now = Start.AddMinutes(5).AddSeconds(30);
        var withinGrace = tracker.Take("early");
        tracker.Track(Started("early-2", expiresAt: Start.AddMinutes(5)));
        clock.Now = Start.AddMinutes(7);

        Assert.IsNotNull(withinGrace);
        Assert.IsNull(tracker.Take("early-2"));
        Assert.IsNotNull(tracker.Take("late"));

        clock.Now = Start + McpSignInTracker.DefaultLifetime + TimeSpan.FromMinutes(2);
        Assert.IsNull(tracker.Take("unsaid"), "a sign-in with no stated expiry doesn't wait forever");
    }

    [TestMethod]
    public void TheTable_IsBounded_AndForgetsTheOldestFirst()
    {
        var clock = new Clock(Start);
        var tracker = new McpSignInTracker(clock);
        for (var i = 0; i <= McpSignInTracker.MaxPending; i++)
        {
            clock.Now = Start.AddSeconds(i);
            tracker.Track(Started($"st-{i}", expiresAt: Start.AddHours(1)));
        }

        Assert.IsNull(tracker.Take("st-0"), "the oldest made room");
        Assert.IsNotNull(tracker.Take("st-1"));
        Assert.IsNotNull(tracker.Take($"st-{McpSignInTracker.MaxPending}"));
    }

    // ── The public address ───────────────────────────────────────────────────

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("  ", null)]
    [DataRow("https://rockbot-agui.example.ts.net", "https://rockbot-agui.example.ts.net")]
    [DataRow("https://rockbot-agui.example.ts.net/", "https://rockbot-agui.example.ts.net")]
    [DataRow(" https://example.com:8443/agui/ ", "https://example.com:8443/agui")]
    [DataRow("http://rockbot-agui", "http://rockbot-agui")]
    public void PublicBaseUrl_IsNormalised(string? configured, string? expected)
    {
        var options = new AgUiOptions { PublicBaseUrl = configured };

        Assert.AreEqual(expected, options.ResolvePublicBaseUrl());
    }

    [TestMethod]
    [DataRow("rockbot-agui.example.ts.net", DisplayName = "no scheme")]
    [DataRow("/agui", DisplayName = "a path")]
    [DataRow("ftp://example.com", DisplayName = "not http")]
    [DataRow("https://example.com/?x=1", DisplayName = "a query")]
    [DataRow("https://example.com/#top", DisplayName = "a fragment")]
    public void PublicBaseUrl_ThatIsNotAnAddress_IsRefused(string configured)
    {
        var options = new AgUiOptions { PublicBaseUrl = configured };

        Assert.ThrowsExactly<InvalidOperationException>(() => options.ResolvePublicBaseUrl());
    }

    [TestMethod]
    public void TheProxy_DoesNotStart_WithABadPublicAddress()
    {
        using var host = new AgUiTestHost(("AgUi:PublicBaseUrl", "rockbot-agui.example.ts.net"));

        Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());
    }
}
