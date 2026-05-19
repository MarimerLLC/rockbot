using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>schedule_task</c> tool: creates or replaces a cron-scheduled task.
/// </summary>
internal sealed class ScheduleTaskExecutor(ISchedulerService scheduler, AgentClock clock) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string name, cron, description;
        bool runOnce;
        ClientCapabilities capabilities;
        try
        {
            var args = ParseArgs(request.Arguments);
            name = GetRequired(args, "name");
            cron = GetRequired(args, "cron");
            description = GetRequired(args, "description");
            runOnce = GetOptionalBool(args, "runOnce");
            capabilities = ParseOutputFormat(GetOptionalString(args, "outputFormat"));
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
                ClientCapabilities: capabilities);

            await scheduler.ScheduleAsync(task, ct);

            var next = scheduler.GetNextOccurrence(task);
            var now = clock.Now;
            var confirmation = next.HasValue
                ? $"Scheduled task '{name}' with cron '{cron}'. Next fire: {next.Value:yyyy-MM-dd HH:mm:ss} ({clock.Zone.Id}). Current time: {now:yyyy-MM-dd HH:mm:ss}."
                : $"Scheduled task '{name}' with cron '{cron}'. WARNING: no future occurrence found — task will never fire. Please cancel and reschedule with a valid future cron.";

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
