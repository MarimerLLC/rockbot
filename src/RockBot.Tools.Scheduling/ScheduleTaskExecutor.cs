using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>schedule_task</c> tool: creates or replaces a cron-scheduled task.
/// </summary>
/// <param name="locationEnabled">
/// True when the agent follows the user's location: <c>timeZone</c> is then required and stored
/// with the task. Otherwise it is ignored and every task runs on home time, as before.
/// </param>
internal sealed class ScheduleTaskExecutor(ISchedulerService scheduler, AgentClock clock, bool locationEnabled = false)
    : IToolExecutor
{
    internal const string TimeZoneChoices =
        "Set timeZone to 'local' (follows the user as they travel — their own day, one-time reminders), " +
        "'home' (home-office hours, other people at home), or a fixed IANA id such as 'Europe/London'.";

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string name, cron, description;
        bool runOnce;
        ClientCapabilities capabilities;
        string? timeZone = null;
        try
        {
            var args = ParseArgs(request.Arguments);
            name = GetRequired(args, "name");
            cron = GetRequired(args, "cron");
            description = GetRequired(args, "description");
            runOnce = GetOptionalBool(args, "runOnce");
            capabilities = ParseOutputFormat(GetOptionalString(args, "outputFormat"));
            if (locationEnabled)
                timeZone = ParseTimeZone(GetOptionalString(args, "timeZone"), runOnce);
        }
        catch (Exception ex)
        {
            return Error(request, ex.Message);
        }

        try
        {
            var task = new ScheduledTask(
                Name: name,
                CronExpression: cron,
                Description: description,
                CreatedAt: DateTimeOffset.UtcNow,
                RunOnce: runOnce,
                ClientCapabilities: capabilities,
                TimeZone: timeZone);

            await scheduler.ScheduleAsync(task, ct);

            var next = scheduler.GetNextOccurrence(task);
            var now = clock.Now;
            string confirmation;
            if (!locationEnabled)
            {
                confirmation = next.HasValue
                    ? $"Scheduled task '{name}' with cron '{cron}'. Next fire: {next.Value:yyyy-MM-dd HH:mm:ss} ({clock.Zone.Id}). Current time: {now:yyyy-MM-dd HH:mm:ss}."
                    : $"Scheduled task '{name}' with cron '{cron}'. WARNING: no future occurrence found — task will never fire. Please cancel and reschedule with a valid future cron.";
            }
            else
            {
                var zone = clock.ResolveZone(timeZone);
                var zoneLabel = DescribeZone(timeZone, zone);
                confirmation = next.HasValue
                    ? $"Scheduled task '{name}' with cron '{cron}' in {zoneLabel}. Next fire: {TimeZoneInfo.ConvertTime(next.Value, zone):yyyy-MM-dd HH:mm:ss} ({zone.Id}). Current time: {now:yyyy-MM-dd HH:mm:ss} ({clock.Zone.Id})."
                    : $"Scheduled task '{name}' with cron '{cron}' in {zoneLabel}. WARNING: no future occurrence found — task will never fire. Please cancel and reschedule with a valid future cron.";
            }

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = confirmation,
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return Error(request, $"Failed to schedule task: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates and normalizes the <c>timeZone</c> argument. A one-time <c>local</c> task is pinned
    /// to the concrete zone the user is in now, so its moment can't drift if they cross zones
    /// before it fires.
    /// </summary>
    private string ParseTimeZone(string? spec, bool runOnce)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new ArgumentException($"Missing required argument: timeZone. {TimeZoneChoices}");
        if (!AgentClock.IsValidSpec(spec))
            throw new ArgumentException($"Unknown timeZone '{spec}'. {TimeZoneChoices}");

        spec = spec.Trim();
        if (spec.Equals(AgentClock.LocalSpec, StringComparison.OrdinalIgnoreCase))
            return runOnce ? clock.Zone.Id : AgentClock.LocalSpec;
        if (spec.Equals(AgentClock.HomeSpec, StringComparison.OrdinalIgnoreCase))
            return AgentClock.HomeSpec;
        return spec;
    }

    /// <summary>"local (Europe/Amsterdam)", "home (America/Chicago)", or the fixed zone id.</summary>
    internal static string DescribeZone(string? spec, TimeZoneInfo resolved) =>
        string.IsNullOrWhiteSpace(spec) || spec.Equals(AgentClock.HomeSpec, StringComparison.OrdinalIgnoreCase)
            ? $"{AgentClock.HomeSpec} ({resolved.Id})"
            : spec.Equals(AgentClock.LocalSpec, StringComparison.OrdinalIgnoreCase)
                ? $"{AgentClock.LocalSpec} ({resolved.Id})"
                : resolved.Id;

    private static Dictionary<string, JsonElement> ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
    }

    private static string GetRequired(Dictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el))
            throw new ArgumentException($"Missing required argument: {key}");
        return el.GetString() ?? throw new ArgumentException($"Argument '{key}' must be a non-null string");
    }

    private static bool GetOptionalBool(Dictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }

    private static string? GetOptionalString(Dictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    /// <summary>
    /// Maps a tool-friendly outputFormat string to a <see cref="ClientCapabilities"/> bitfield.
    /// Three presets:
    /// <list type="bullet">
    /// <item><c>"plain"</c> / null / unrecognised → <see cref="ClientCapabilities.None"/></item>
    /// <item><c>"markdown"</c> → headings, tables, code, links — the universal rich-text set</item>
    /// <item><c>"rich"</c> → <see cref="ClientCapabilityPresets.Blazor"/> (HTML + SVG)</item>
    /// </list>
    /// </summary>
    internal static ClientCapabilities ParseOutputFormat(string? format) =>
        format?.Trim().ToLowerInvariant() switch
        {
            "markdown" =>
                ClientCapabilities.Text | ClientCapabilities.MarkdownBasic |
                ClientCapabilities.MarkdownHeadings | ClientCapabilities.MarkdownTables |
                ClientCapabilities.MarkdownCode | ClientCapabilities.LinkInline |
                ClientCapabilities.MarkdownStrikethrough,
            "rich" => ClientCapabilityPresets.Blazor,
            _ => ClientCapabilities.None,
        };

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Content = message,
        IsError = true
    };
}
