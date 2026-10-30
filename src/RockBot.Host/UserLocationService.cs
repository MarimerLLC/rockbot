using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Polls the configured <see cref="IUserLocationProvider"/> and keeps <see cref="UserLocationState"/>
/// and <see cref="AgentClock.SetCurrentZone"/> in step with the user's latest fix. A fix older
/// than <see cref="UserLocationOptions.MaxAge"/> (or no fix at all) clears both, reverting the
/// agent to the home zone. Fetch failures keep the last good state.
/// </summary>
internal sealed class UserLocationService(
    IUserLocationProvider provider,
    UserLocationState state,
    AgentClock clock,
    IOptions<UserLocationOptions> options,
    ILogger<UserLocationService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly UserLocationOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsEnabled)
        {
            logger.LogInformation("User location disabled: UserLocation:OwnTracks:BaseUrl is not set");
            return;
        }

        logger.LogInformation("User location: polling every {Interval}", _options.PollInterval);

        using var timer = new PeriodicTimer(_options.PollInterval, _time);
        do
        {
            await PollOnceAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        if (!_options.IsEnabled)
            return;

        UserLocation? fix;
        try
        {
            fix = await provider.GetLatestAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!_failing)
                logger.LogWarning(ex, "User location: fetch failed — keeping last known state");
            _failing = true;

            // A source that stays down past MaxAge must not pin the last trip's zone forever.
            if (state.Current is { } last && IsStale(last))
                Apply(null);
            return;
        }

        if (_failing)
            logger.LogInformation("User location: fetch recovered");
        _failing = false;

        Apply(fix is not null && !IsStale(fix) ? fix : null);
    }

    private bool IsStale(UserLocation fix) => _time.GetUtcNow() - fix.ReportedAt > _options.MaxAge;

    private void Apply(UserLocation? fix)
    {
        var previous = state.Current;
        state.Current = fix;

        if (fix is null)
        {
            clock.SetCurrentZone(null);
            if (previous is not null)
                logger.LogInformation("User location: no fresh fix — reverting to home zone {Zone}", clock.HomeZone.Id);
            return;
        }

        if (fix.TimeZoneId is { } tzId)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out var zone))
                clock.SetCurrentZone(zone);
            else
                logger.LogWarning("User location: unknown timezone '{Zone}' in fix — keeping current zone", tzId);
        }

        if (previous is null
            || previous.TimeZoneId != fix.TimeZoneId
            || previous.Locality != fix.Locality)
        {
            logger.LogInformation("User location: zone {Zone} ({Locality}), fix age {Age}",
                clock.Zone.Id, fix.Locality ?? "no locality", _time.GetUtcNow() - fix.ReportedAt);
        }
    }
}
