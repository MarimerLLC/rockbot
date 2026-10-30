using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Reads the user's latest fix from an OwnTracks Recorder's <c>/api/0/last</c> endpoint.
/// Only the fields the agent uses are requested, so street addresses, battery and the like
/// never enter the process.
/// </summary>
internal sealed class OwnTracksRecorderLocationProvider : IUserLocationProvider, IDisposable
{
    internal const string Fields = "tst,lat,lon,acc,tzname,locality,cc";

    // Long-lived on purpose: Host has no IHttpClientFactory, and one client per process is the
    // documented alternative.
    private readonly HttpClient _http;
    private readonly UserLocationOptions.OwnTracksOptions _options;

    public OwnTracksRecorderLocationProvider(IOptions<UserLocationOptions> options)
        : this(options, new HttpClientHandler())
    {
    }

    internal OwnTracksRecorderLocationProvider(IOptions<UserLocationOptions> options, HttpMessageHandler handler)
    {
        _options = options.Value.OwnTracks;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<UserLocation?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(BuildRequestUri(_options), cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseNewest(doc.RootElement);
    }

    internal static Uri BuildRequestUri(UserLocationOptions.OwnTracksOptions options)
    {
        var baseUrl = (options.BaseUrl ?? string.Empty).TrimEnd('/');
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.User))
            query.Add("user=" + Uri.EscapeDataString(options.User));
        if (!string.IsNullOrWhiteSpace(options.Device))
            query.Add("device=" + Uri.EscapeDataString(options.Device));
        query.Add("fields=" + Fields);

        return new Uri($"{baseUrl}/api/0/last?{string.Join('&', query)}");
    }

    internal static UserLocation? ParseNewest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return null;

        UserLocation? newest = null;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryGetDouble(item, "tst", out var tst)
                || !TryGetDouble(item, "lat", out var lat)
                || !TryGetDouble(item, "lon", out var lon))
            {
                continue;
            }

            var fix = new UserLocation(
                Latitude: lat,
                Longitude: lon,
                AccuracyMeters: TryGetDouble(item, "acc", out var acc) ? acc : null,
                ReportedAt: DateTimeOffset.FromUnixTimeSeconds((long)tst),
                TimeZoneId: GetString(item, "tzname"),
                Locality: GetString(item, "locality"),
                CountryCode: GetString(item, "cc"));

            if (newest is null || fix.ReportedAt > newest.ReportedAt)
                newest = fix;
        }

        return newest;
    }

    private static bool TryGetDouble(JsonElement item, string name, out double value)
    {
        value = 0;
        return item.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetDouble(out value);
    }

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(el.GetString())
            ? el.GetString()
            : null;

    public void Dispose() => _http.Dispose();
}
