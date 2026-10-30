using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

/// <summary>
/// <see cref="AgentClock"/> home zone vs current zone (#740).
/// </summary>
[TestClass]
public sealed class AgentClockTests
{
    internal static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    internal static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    internal static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    /// <summary>A clock whose home zone is <paramref name="homeZoneId"/>, with no persisted timezone file.</summary>
    internal static AgentClock MakeClock(string homeZoneId = "America/Chicago") =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:Timezone"] = homeZoneId })
                .Build(),
            Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions
            {
                BasePath = Path.Combine(Path.GetTempPath(), "rockbot-clock-" + Guid.NewGuid().ToString("N"))
            }),
            NullLogger<AgentClock>.Instance);

    [TestMethod]
    public void Zone_DefaultsToHome()
    {
        var clock = MakeClock();

        Assert.AreEqual("America/Chicago", clock.Zone.Id);
        Assert.AreEqual("America/Chicago", clock.HomeZone.Id);
        Assert.IsFalse(clock.IsAwayFromHome);
    }

    [TestMethod]
    public void SetCurrentZone_OverridesZoneAndNow_ButNotHome()
    {
        var clock = MakeClock();

        clock.SetCurrentZone(Amsterdam);

        Assert.AreEqual("Europe/Amsterdam", clock.Zone.Id);
        Assert.AreEqual("America/Chicago", clock.HomeZone.Id);
        Assert.IsTrue(clock.IsAwayFromHome);
        Assert.AreEqual(Amsterdam.GetUtcOffset(DateTimeOffset.UtcNow), clock.Now.Offset);
    }

    [TestMethod]
    public void SetCurrentZone_Null_RevertsToHome()
    {
        var clock = MakeClock();
        clock.SetCurrentZone(Amsterdam);

        clock.SetCurrentZone(null);

        Assert.AreEqual("America/Chicago", clock.Zone.Id);
        Assert.IsFalse(clock.IsAwayFromHome);
    }

    [TestMethod]
    public void SetCurrentZone_ToHomeZone_IsNotAway()
    {
        var clock = MakeClock();

        clock.SetCurrentZone(Chicago);

        Assert.IsFalse(clock.IsAwayFromHome);
    }

    [TestMethod]
    public void ZoneChanged_FiresOnlyWhenEffectiveZoneChanges()
    {
        var clock = MakeClock();
        var fired = 0;
        clock.ZoneChanged += (_, _) => fired++;

        clock.SetCurrentZone(Chicago);    // same as home: no change
        Assert.AreEqual(0, fired);

        clock.SetCurrentZone(Amsterdam);
        Assert.AreEqual(1, fired);

        clock.SetCurrentZone(Amsterdam);  // repeat fix in the same zone
        Assert.AreEqual(1, fired);

        clock.SetCurrentZone(null);
        Assert.AreEqual(2, fired);

        clock.SetCurrentZone(null);
        Assert.AreEqual(2, fired);
    }

    [TestMethod]
    public async Task SetZoneAsync_DoesNotRaiseZoneChanged()
    {
        var clock = MakeClock();
        var fired = 0;
        clock.ZoneChanged += (_, _) => fired++;

        await clock.SetZoneAsync(Tokyo);

        Assert.AreEqual(0, fired);
        Assert.AreEqual("Asia/Tokyo", clock.HomeZone.Id);
    }

    [TestMethod]
    public void ResolveZone_HomeLocalIanaAndUnknown()
    {
        var clock = MakeClock();
        clock.SetCurrentZone(Amsterdam);

        Assert.AreEqual("America/Chicago", clock.ResolveZone(null).Id);
        Assert.AreEqual("America/Chicago", clock.ResolveZone("home").Id);
        Assert.AreEqual("America/Chicago", clock.ResolveZone("HOME").Id);
        Assert.AreEqual("Europe/Amsterdam", clock.ResolveZone("local").Id);
        Assert.AreEqual("Asia/Tokyo", clock.ResolveZone("Asia/Tokyo").Id);
        Assert.AreEqual("America/Chicago", clock.ResolveZone("Not/AZone").Id);
    }

    [TestMethod]
    public void IsValidSpec_AcceptsHomeLocalAndIana()
    {
        Assert.IsTrue(AgentClock.IsValidSpec("home"));
        Assert.IsTrue(AgentClock.IsValidSpec("Local"));
        Assert.IsTrue(AgentClock.IsValidSpec("Europe/London"));
        Assert.IsFalse(AgentClock.IsValidSpec("Not/AZone"));
        Assert.IsFalse(AgentClock.IsValidSpec(""));
        Assert.IsFalse(AgentClock.IsValidSpec(null));
    }
}
