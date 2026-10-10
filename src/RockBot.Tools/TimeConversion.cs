using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Tools;

/// <summary>
/// Deterministic wall-clock conversion between IANA time zones for a given date (#685). Models
/// did this arithmetic in their heads and got DST gaps wrong: 09:45 Europe/Amsterdam on
/// 2026-10-28 is 03:45 America/Chicago (the EU left summer time on Oct 25, the US does on Nov 1),
/// not 02:45.
/// </summary>
public static class TimeConversion
{
    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd",
    ];

    /// <summary>
    /// Converts <paramref name="dateTime"/>, a wall-clock time in <paramref name="fromZoneId"/>,
    /// to <paramref name="toZoneId"/>. Returns a one-line description with both UTC offsets, or an
    /// error message starting with <c>Error:</c>.
    /// </summary>
    /// <param name="dateTime">ISO 8601 date and time, e.g. <c>2026-10-28T09:45</c>. An explicit
    /// offset (<c>…+01:00</c>, <c>…Z</c>) is honoured and <paramref name="fromZoneId"/> may then be omitted.</param>
    /// <param name="fromZoneId">IANA id of the zone the time is in.</param>
    /// <param name="toZoneId">IANA id of the zone to convert to.</param>
    public static string Convert(string? dateTime, string? fromZoneId, string? toZoneId)
    {
        if (string.IsNullOrWhiteSpace(dateTime))
            return "Error: datetime is required, e.g. '2026-10-28T09:45'.";
        if (!TryFindZone(toZoneId, out var to, out var toError))
            return toError;

        var text = dateTime.Trim();
        DateTimeOffset instant;
        TimeZoneInfo? from = null;
        string? note = null;

        if (HasExplicitOffset(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            instant = withOffset;
            if (!string.IsNullOrWhiteSpace(fromZoneId))
            {
                if (!TryFindZone(fromZoneId, out from, out var fromError)) return fromError;
            }
        }
        else
        {
            if (!TryFindZone(fromZoneId, out from, out var fromError))
                return fromError;
            if (!DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var local))
                return $"Error: could not read '{dateTime}' as a date and time. Use ISO 8601, e.g. '2026-10-28T09:45'.";
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            if (from!.IsInvalidTime(local))
                return $"Error: {local:yyyy-MM-dd HH:mm} does not exist in {from.Id} — the clocks jump forward " +
                       "over it for daylight saving time.";

            var offset = from.GetUtcOffset(local);
            if (from.IsAmbiguousTime(local))
            {
                // The repeated hour when clocks go back: report the first (daylight) occurrence.
                var offsets = from.GetAmbiguousTimeOffsets(local);
                offset = offsets.Max();
                note = $" Note: {local:HH:mm} occurs twice in {from.Id} that day; this is the first occurrence " +
                       $"(UTC{FormatOffset(offset)}). The second is UTC{FormatOffset(offsets.Min())}.";
            }
            instant = new DateTimeOffset(local, offset);
        }

        var converted = TimeZoneInfo.ConvertTime(instant, to);
        var sourceZone = from?.Id ?? "the given offset";
        var source = from is null ? instant : TimeZoneInfo.ConvertTime(instant, from);

        return $"{source:yyyy-MM-dd HH:mm} {sourceZone} (UTC{FormatOffset(source.Offset)}) = " +
               $"{converted:yyyy-MM-dd HH:mm} {to.Id} (UTC{FormatOffset(converted.Offset)}), " +
               $"{converted.ToString("dddd h:mm tt", CultureInfo.InvariantCulture)} local time; " +
               $"{instant.UtcDateTime:yyyy-MM-dd HH:mm} UTC." + note;
    }

    private static bool HasExplicitOffset(string text)
    {
        if (text.EndsWith('Z') || text.EndsWith('z')) return true;
        var t = text.IndexOf('T') >= 0 ? text.IndexOf('T') : text.IndexOf(' ');
        if (t < 0) return false;
        var timePart = text[(t + 1)..];
        return timePart.Contains('+') || timePart.Contains('-');
    }

    private static bool TryFindZone(string? id, out TimeZoneInfo zone, out string error)
    {
        zone = TimeZoneInfo.Utc;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Error: a time zone is required. Use an IANA id such as 'America/Chicago' or 'Europe/Amsterdam'.";
            return false;
        }
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = $"Error: unknown time zone '{id}'. Use an IANA id such as 'America/Chicago' or 'Europe/Amsterdam'.";
            return false;
        }
    }

    private static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}

/// <summary>The <c>convert_time</c> tool (#685).</summary>
internal sealed class ConvertTimeExecutor : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
        }
        catch (JsonException)
        {
            args = [];
        }

        var content = TimeConversion.Convert(
            Read(args, "datetime"), Read(args, "from_timezone"), Read(args, "to_timezone"));
        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = content.StartsWith("Error:", StringComparison.Ordinal)
        });
    }

    private static string? Read(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}

/// <summary>Registers <c>convert_time</c> with the tool registry at startup (#685).</summary>
internal sealed class TimeToolRegistrar(IToolRegistry registry, ILogger<TimeToolRegistrar> logger) : IHostedService
{
    internal const string ToolName = "convert_time";

    internal const string Schema = """
        {
          "type": "object",
          "properties": {
            "datetime": {
              "type": "string",
              "description": "The wall-clock date and time to convert, ISO 8601 (e.g. '2026-10-28T09:45'). An explicit offset ('…+01:00', '…Z') is honoured."
            },
            "from_timezone": {
              "type": "string",
              "description": "IANA time zone the datetime is in, e.g. 'Europe/Amsterdam'."
            },
            "to_timezone": {
              "type": "string",
              "description": "IANA time zone to convert to, e.g. 'America/Chicago'."
            }
          },
          "required": ["datetime", "from_timezone", "to_timezone"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Register(new ToolRegistration
        {
            Name = ToolName,
            Description =
                "Convert a date and time from one IANA time zone to another, using the daylight-saving rules " +
                "in force on that date. Returns both times with their UTC offsets. Use it instead of working " +
                "out offsets yourself whenever a time crosses zones: regions change their clocks on different " +
                "dates, so an offset that holds today can be wrong for the date in question.",
            ParametersSchema = Schema,
            Source = "time"
        }, new ConvertTimeExecutor());

        logger.LogInformation("Registered tool: {Tool}", ToolName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>DI registration for the time tools.</summary>
public static class TimeToolServiceCollectionExtensions
{
    /// <summary>Registers the <c>convert_time</c> tool (#685). Requires <see cref="ToolServiceCollectionExtensions.AddToolHandler"/>.</summary>
    public static AgentHostBuilder AddTimeTools(this AgentHostBuilder builder)
    {
        builder.Services.AddHostedService<TimeToolRegistrar>();
        return builder;
    }
}
