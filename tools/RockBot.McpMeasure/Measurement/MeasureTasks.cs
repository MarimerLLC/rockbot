using System.Text.Json;
using RockBot.McpMeasure.Fixtures;

namespace RockBot.McpMeasure.Measurement;

/// <summary>
/// One user turn: the prompt, and the downstream call that carries it out. <see cref="Check"/>
/// says whether a call's arguments do what the user asked (null when they do), beyond being
/// valid for the schema. <see cref="ScriptedArgs"/> is what the scripted model sends, and doubles
/// as an example of a correct call.
/// </summary>
public sealed record MeasureTurn(
    string Prompt,
    string Server,
    string Tool,
    IReadOnlyDictionary<string, object?> ScriptedArgs,
    Func<JsonElement, string?> Check,
    string FindQuery);

/// <summary>A task: one turn, or a short conversation in one session.</summary>
public sealed record MeasureTask(string Id, IReadOnlyList<MeasureTurn> Turns);

/// <summary>
/// The fixed task set. The first four follow mcp-aggregator#42's (send mail, tomorrow's calendar,
/// a folder on one of two look-alike OneDrives), with a todo in place of its docs search. The
/// last is two turns on one server, where pinned mode should differ from lazy: the second turn
/// needs a tool the first didn't use.
/// </summary>
public static class MeasureTasks
{
    public static IReadOnlyList<MeasureTask> All { get; } =
    [
        new("calendar_tomorrow",
        [
            new("What's on my calendar tomorrow?",
                FixtureServers.Adjutant, "get_calendar_events",
                new Dictionary<string, object?>
                {
                    ["timeZone"] = "America/Chicago",
                    ["startDate"] = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd"),
                    ["endDate"] = DateTime.UtcNow.Date.AddDays(2).ToString("yyyy-MM-dd"),
                },
                args => NonEmpty(args, "timeZone"),
                "calendar events"),
        ]),

        new("send_email",
        [
            new("Send an email to alice@example.com with the subject \"Lunch Thursday?\" and the message \"Are you free for lunch on Thursday at noon?\"",
                FixtureServers.Adjutant, "send_email",
                new Dictionary<string, object?>
                {
                    ["to"] = new[] { "alice@example.com" },
                    ["subject"] = "Lunch Thursday?",
                    ["body"] = "Are you free for lunch on Thursday at noon?",
                },
                args => ArrayHas(args, "to", "alice@example.com") ?? NonEmpty(args, "subject") ?? NonEmpty(args, "body"),
                "send email"),
        ]),

        new("list_files_marimer",
        [
            new("List the files in the Documents/Reports folder of the Marimer company OneDrive.",
                FixtureServers.OneDriveMarimer, "list_files",
                new Dictionary<string, object?> { ["folder_path"] = "Documents/Reports" },
                args => Contains(args, "folder_path", "Reports"),
                "list files marimer onedrive"),
        ]),

        new("add_todo",
        [
            new("Add a todo to renew the marimer.example domain registration, due next Friday.",
                FixtureServers.Todo, "add_task",
                new Dictionary<string, object?>
                {
                    ["title"] = "Renew the marimer.example domain registration",
                    ["due_date"] = NextFriday().ToString("yyyy-MM-dd"),
                },
                args => Contains(args, "title", "domain") ?? NonEmpty(args, "due_date"),
                "add task todo"),
        ]),

        new("reply_thread",
        [
            new("Find the email from Dana about the Q3 offsite.",
                FixtureServers.Adjutant, "search_emails",
                new Dictionary<string, object?> { ["query"] = "Dana Q3 offsite" },
                args => NonEmpty(args, "query"),
                "search emails"),
            new("Reply to Dana that I can make it.",
                FixtureServers.Adjutant, "send_email",
                new Dictionary<string, object?>
                {
                    ["to"] = new[] { FixtureServers.DanaAddress },
                    ["subject"] = "Re: Q3 offsite: are you in?",
                    ["body"] = "Count me in for the Q3 offsite.",
                },
                args => ArrayHas(args, "to", FixtureServers.DanaAddress) ?? NonEmpty(args, "body"),
                "send email reply"),
        ]),
    ];

    private static DateTime NextFriday()
    {
        var today = DateTime.UtcNow.Date;
        var days = ((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }

    private static string? NonEmpty(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? null
            : $"'{key}' is missing or empty";

    private static string? Contains(JsonElement args, string key, string text) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString()!.Contains(text, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"'{key}' doesn't mention '{text}'";

    private static string? ArrayHas(JsonElement args, string key, string value) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            && v.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String
                && string.Equals(e.GetString(), value, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"'{key}' is not an array containing {value}";
}
