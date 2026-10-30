using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// <see cref="UserLocationService"/> and <see cref="AgentHostBuilderUserLocationExtensions.WithUserLocation"/> (#740).
/// </summary>
[TestClass]
public sealed class UserLocationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private static UserLocationOptions Enabled() => new()
    {
        OwnTracks = { BaseUrl = "http://recorder.test:8083", User = "rocky" }
    };

    private static (UserLocationService Service, UserLocationState State, AgentClock Clock) Make(
        IUserLocationProvider provider, UserLocationOptions? options = null)
    {
        var opts = Options.Create(options ?? Enabled());
        var state = new UserLocationState(opts);
        var clock = AgentClockTests.MakeClock();
        var service = new UserLocationService(
            provider, state, clock, opts, NullLogger<UserLocationService>.Instance, new FixedTime(Now));
        return (service, state, clock);
    }

    private static UserLocation Fix(DateTimeOffset at, string? tz = "Europe/Amsterdam") =>
        new(52.4, 4.9, 3, at, tz, "Amsterdam", "NL");

    [TestMethod]
    public async Task FreshFix_SetsCurrentZoneAndState()
    {
        var fix = Fix(Now.AddMinutes(-2));
        var (service, state, clock) = Make(new StubProvider(fix));

        await service.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual("Europe/Amsterdam", clock.Zone.Id);
        Assert.AreEqual("America/Chicago", clock.HomeZone.Id);
        Assert.AreSame(fix, state.Current);
    }

    [TestMethod]
    public async Task StaleFix_ClearsZoneAndState()
    {
        var provider = new StubProvider(Fix(Now.AddMinutes(-2)));
        var (service, state, clock) = Make(provider);
        await service.PollOnceAsync(CancellationToken.None);

        provider.Next = Fix(Now.AddHours(-73));
        await service.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual("America/Chicago", clock.Zone.Id);
        Assert.IsNull(state.Current);
    }

    [TestMethod]
    public async Task NoFix_LeavesHomeZone()
    {
        var (service, state, clock) = Make(new StubProvider(null));

        await service.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual("America/Chicago", clock.Zone.Id);
        Assert.IsNull(state.Current);
    }

    [TestMethod]
    public async Task FetchFailure_KeepsLastGoodState()
    {
        var fix = Fix(Now.AddMinutes(-2));
        var provider = new StubProvider(fix);
        var (service, state, clock) = Make(provider);
        await service.PollOnceAsync(CancellationToken.None);

        provider.Throw = true;
        await service.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual("Europe/Amsterdam", clock.Zone.Id);
        Assert.AreSame(fix, state.Current);
    }

    [TestMethod]
    public async Task FixWithUnknownZone_KeepsZone_ButRecordsLocation()
    {
        var (service, state, clock) = Make(new StubProvider(Fix(Now.AddMinutes(-2), tz: "Not/AZone")));

        await service.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual("America/Chicago", clock.Zone.Id);
        Assert.IsNotNull(state.Current);
    }

    [TestMethod]
    public async Task DisabledConfig_DoesNothing()
    {
        var provider = new StubProvider(Fix(Now.AddMinutes(-2)));
        var (service, state, clock) = Make(provider, new UserLocationOptions());

        await service.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(0, provider.Calls);
        Assert.AreEqual("America/Chicago", clock.Zone.Id);
        Assert.IsNull(state.Current);
    }

    [TestMethod]
    public void WithUserLocation_NoBaseUrl_RegistersNothing()
    {
        var baseline = new ServiceCollection();
        baseline.AddRockBotHost(agent => agent.WithIdentity("a"));

        var services = new ServiceCollection();
        services.AddRockBotHost(agent => agent
            .WithIdentity("a")
            .WithUserLocation(o => o.OwnTracks.User = "rocky"));

        Assert.AreEqual(baseline.Count, services.Count);
        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(UserLocationState)));
    }

    [TestMethod]
    public void WithUserLocation_WithBaseUrl_RegistersStateProviderAndService()
    {
        var services = new ServiceCollection();
        services.AddRockBotHost(agent => agent
            .WithIdentity("a")
            .WithUserLocation(o => o.OwnTracks.BaseUrl = "http://recorder.test:8083"));

        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(UserLocationState)));
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IUserLocationProvider)));
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(UserLocationService)));
    }

    private sealed class StubProvider(UserLocation? fix) : IUserLocationProvider
    {
        public UserLocation? Next { get; set; } = fix;
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<UserLocation?> GetLatestAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw) throw new HttpRequestException("recorder down");
            return Task.FromResult(Next);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
