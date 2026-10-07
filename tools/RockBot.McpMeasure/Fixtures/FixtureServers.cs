using System.Collections.Concurrent;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RockBot.McpMeasure.Fixtures;

/// <summary>One downstream call as a fixture server received it, after the SDK bound the arguments.</summary>
public sealed record RecordedCall(string Server, string Tool, IReadOnlyDictionary<string, object?> Args, bool Valid, string? Problem);

/// <summary>Every call the fixture servers receive, in order, across all servers.</summary>
public sealed class CallRecorder
{
    private readonly ConcurrentQueue<RecordedCall> _calls = new();

    public int Count => _calls.Count;

    public void Add(RecordedCall call) => _calls.Enqueue(call);

    /// <summary>The calls recorded after the first <paramref name="since"/>.</summary>
    public IReadOnlyList<RecordedCall> Since(int since) => [.. _calls.Skip(since)];
}

/// <summary>A fixture MCP server: its name in <c>mcp.json</c>, what it tells the model, and its tools.</summary>
public sealed record FixtureServer(string Name, string Title, string Instructions, IReadOnlyList<McpServerTool> Tools);

/// <summary>An item of the bulk email tools.</summary>
public sealed record EmailRef(
    [property: Description("Account the email is in")] string AccountId,
    [property: Description("Email id")] string EmailId);

/// <summary>
/// In-process stand-ins for the MCP servers RockBot calls in production, ported from
/// mcp-aggregator#42's stubs and widened to the real surfaces:
/// <list type="bullet">
///   <item><c>adjutant</c>: the 29 tools of calendar-mcp, with their parameter names, required
///   arguments, arrays and dates, and its "call list_accounts first" instructions.</item>
///   <item><c>onedrive-marimer</c> and <c>onedrive-personal</c>: identical tool names, so the
///   model has to pick the server.</item>
///   <item><c>todo</c>: the TodoApp tools.</item>
/// </list>
/// The schemas are what matter, since they are what the model writes arguments against. The
/// tools a task targets validate what they receive, and every call is recorded, so the harness
/// knows whether the right tool got usable arguments whatever the model says afterwards.
/// </summary>
public static class FixtureServers
{
    public const string Adjutant = "adjutant";
    public const string OneDriveMarimer = "onedrive-marimer";
    public const string OneDrivePersonal = "onedrive-personal";
    public const string Todo = "todo";

    public static IReadOnlyList<FixtureServer> All(CallRecorder recorder) =>
    [
        new(Adjutant, "Calendar MCP",
            "Unified access to email, calendar, and contacts across the user's Microsoft 365, Google and " +
            "Outlook.com accounts. Call list_accounts first and use the accountId values it returns. " +
            "Call get_guide for playbooks.",
            AdjutantTools(recorder)),
        new(OneDriveMarimer, "OneDrive (Marimer)", "Files in the Marimer LLC company OneDrive.",
            OneDriveTools(OneDriveMarimer, recorder)),
        new(OneDrivePersonal, "OneDrive (Personal)", "Files in the user's personal OneDrive.",
            OneDriveTools(OneDrivePersonal, recorder)),
        new(Todo, "Todo", "The user's task list: add, list, complete and update tasks with due dates.",
            TodoTools(recorder)),
    ];

    // ── adjutant (calendar-mcp) ───────────────────────────────────────────────

    public const string DanaAddress = "dana@contoso.example";

    private static List<McpServerTool> AdjutantTools(CallRecorder r)
    {
        const string s = Adjutant;
        return
        [
            Tool("list_accounts", "List the email/calendar accounts with provider, domains and capabilities.",
                () => Record(r, s, "list_accounts", Args(), null,
                    """[{"accountId":"marimer","provider":"microsoft365","email":"rocky@marimer.example","default":true},{"accountId":"personal","provider":"outlook.com","email":"rocky@example.com"}]""")),

            Tool("list_calendars", "List calendars (id, accountId, name, canEdit, isDefault).",
                ([Description("Account id; omit for all accounts")] string? accountId = null) =>
                    Record(r, s, "list_calendars", Args(("accountId", accountId)), null,
                        """[{"id":"primary","accountId":"marimer","name":"Calendar","canEdit":true,"isDefault":true}]""")),

            Tool("get_calendar_events", "Get calendar events in a date range, with UTC and local times.",
                ([Description("IANA or Windows time zone for local times, e.g. 'America/Chicago'")] string timeZone,
                 [Description("Range start; defaults to now")] DateTime? startDate = null,
                 [Description("Range end; defaults to 7 days after start")] DateTime? endDate = null,
                 [Description("Account id; omit for all accounts")] string? accountId = null,
                 [Description("Calendar id, or 'primary'")] string? calendarId = null,
                 [Description("Maximum events")] int count = 50) =>
                {
                    var problem = string.IsNullOrWhiteSpace(timeZone) ? "'timeZone' is empty"
                        : startDate is { } a && endDate is { } b && b <= a ? "'endDate' must be after 'startDate'"
                        : null;
                    return Record(r, s, "get_calendar_events",
                        Args(("timeZone", timeZone), ("startDate", startDate), ("endDate", endDate),
                            ("accountId", accountId), ("calendarId", calendarId), ("count", count)),
                        problem,
                        """[{"eventId":"evt-1","subject":"Team sync","start":"09:00","end":"09:30"},{"eventId":"evt-2","subject":"1:1 with Sam","start":"14:00","end":"14:30"}]""");
                }),

            Tool("get_calendar_event_details", "Get full details of one calendar event.",
                ([Description("Time zone for local times")] string timeZone,
                 [Description("Account id")] string accountId,
                 [Description("Calendar id")] string calendarId,
                 [Description("Event id")] string eventId) =>
                    Record(r, s, "get_calendar_event_details",
                        Args(("timeZone", timeZone), ("accountId", accountId), ("calendarId", calendarId), ("eventId", eventId)),
                        null, """{"eventId":"evt-1","subject":"Team sync","attendees":["sam@marimer.example"]}""")),

            Tool("create_event", "Create a calendar event.",
                ([Description("Event subject")] string subject,
                 [Description("Start date and time")] DateTime start,
                 [Description("End date and time")] DateTime end,
                 [Description("Account id; omit for the default")] string? accountId = null,
                 [Description("Calendar id")] string? calendarId = null,
                 [Description("Location")] string? location = null,
                 [Description("Attendee email addresses")] string[]? attendees = null,
                 [Description("Body text")] string? body = null,
                 [Description("Time zone of start and end")] string? timeZone = null) =>
                    Record(r, s, "create_event",
                        Args(("subject", subject), ("start", start), ("end", end), ("accountId", accountId),
                            ("attendees", attendees), ("timeZone", timeZone)),
                        string.IsNullOrWhiteSpace(subject) ? "'subject' is empty" : end <= start ? "'end' must be after 'start'" : null,
                        """{"eventId":"evt-new","created":true}""")),

            Tool("update_event", "Update an existing calendar event; only the fields given change.",
                ([Description("Account id")] string accountId,
                 [Description("Calendar id")] string calendarId,
                 [Description("Event id")] string eventId,
                 [Description("New subject")] string? subject = null,
                 [Description("New start")] DateTime? start = null,
                 [Description("New end")] DateTime? end = null,
                 [Description("New location")] string? location = null,
                 [Description("New attendee list")] string[]? attendees = null,
                 [Description("Time zone of start and end")] string? timeZone = null) =>
                    Record(r, s, "update_event", Args(("accountId", accountId), ("eventId", eventId)), null, """{"updated":true}""")),

            Tool("delete_event", "Delete a calendar event.",
                ([Description("Event id")] string eventId,
                 [Description("Account id")] string? accountId = null,
                 [Description("Calendar id")] string? calendarId = null) =>
                    Record(r, s, "delete_event", Args(("eventId", eventId)), null, """{"deleted":true}""")),

            Tool("respond_to_event", "Accept, tentatively accept or decline a meeting invitation.",
                ([Description("Event id")] string eventId,
                 [Description("'accept', 'tentative' or 'decline'")] string response,
                 [Description("Account id")] string? accountId = null,
                 [Description("Calendar id")] string? calendarId = null,
                 [Description("Optional comment to the organizer")] string? comment = null) =>
                    Record(r, s, "respond_to_event", Args(("eventId", eventId), ("response", response)), null, """{"responded":true}""")),

            Tool("get_emails", "Get recent emails, newest first.",
                ([Description("Account id; omit for all accounts")] string? accountId = null,
                 [Description("Maximum emails")] int count = 20,
                 [Description("Only unread emails")] bool unreadOnly = false) =>
                    Record(r, s, "get_emails", Args(("accountId", accountId), ("count", count), ("unreadOnly", unreadOnly)), null,
                        """[{"emailId":"m-100","from":"news@vendor.example","subject":"Weekly digest"}]""")),

            Tool("search_emails", "Search emails by keyword across accounts.",
                ([Description("Search text: words from the subject, body or sender")] string query,
                 [Description("Account id; omit for all accounts")] string? accountId = null,
                 [Description("Maximum results")] int count = 20,
                 [Description("Only emails on or after this date")] DateTime? fromDate = null,
                 [Description("Only emails on or before this date")] DateTime? toDate = null) =>
                    Record(r, s, "search_emails",
                        Args(("query", query), ("accountId", accountId), ("count", count), ("fromDate", fromDate), ("toDate", toDate)),
                        string.IsNullOrWhiteSpace(query) ? "'query' is empty" : null,
                        $$"""[{"accountId":"marimer","emailId":"m-417","from":"Dana Whitfield <{{DanaAddress}}>","subject":"Q3 offsite: are you in?","received":"2026-10-05T16:12:00Z","preview":"We're booking the venue for the Q3 offsite on the 23rd. Can you make it?"}]""")),

            Tool("get_email_details", "Get an email's full body and attachment list.",
                ([Description("Account id")] string accountId,
                 [Description("Email id")] string emailId) =>
                    Record(r, s, "get_email_details", Args(("accountId", accountId), ("emailId", emailId)), null,
                        $$"""{"emailId":"{{emailId}}","from":"{{DanaAddress}}","subject":"Q3 offsite: are you in?","body":"We're booking the venue for the Q3 offsite on the 23rd. Can you make it?"}""")),

            Tool("get_email_attachment", "Fetch an email attachment.",
                ([Description("Account id")] string accountId,
                 [Description("Email id")] string emailId,
                 [Description("Attachment id")] string attachmentId,
                 [Description("'stash' or 'inline'")] string mode = "stash") =>
                    Record(r, s, "get_email_attachment", Args(("emailId", emailId), ("attachmentId", attachmentId)), null, """{"stashed":true}""")),

            Tool("send_email", "Send an email. Picks the sending account from the recipients' domains when accountId is omitted.",
                ([Description("Recipient email addresses")] string[] to,
                 [Description("Subject line")] string subject,
                 [Description("Message body")] string body = "",
                 [Description("Account to send from")] string? accountId = null,
                 [Description("'html', 'text' or 'multipart'")] string bodyFormat = "html",
                 [Description("CC email addresses")] string[]? cc = null,
                 [Description("Plain-text body, for multipart")] string? textBody = null,
                 [Description("HTML body, for multipart")] string? htmlBody = null) =>
                {
                    var problem = to is not { Length: > 0 } ? "'to' must contain at least one address"
                        : to.Any(a => !a.Contains('@')) ? "'to' contains an invalid address"
                        : string.IsNullOrWhiteSpace(subject) ? "'subject' is empty"
                        : string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(textBody) && string.IsNullOrWhiteSpace(htmlBody)
                            ? "the message has no body"
                            : null;
                    return Record(r, s, "send_email",
                        Args(("to", to), ("subject", subject), ("body", body), ("accountId", accountId), ("cc", cc)),
                        problem, $"Sent to {string.Join(", ", to ?? [])}: '{subject}'. Message id msg_{Guid.NewGuid():N}.");
                }),

            Tool("delete_email", "Delete an email.",
                ([Description("Account id")] string accountId, [Description("Email id")] string emailId) =>
                    Record(r, s, "delete_email", Args(("emailId", emailId)), null, """{"deleted":true}""")),

            Tool("move_email", "Move or archive an email, or apply a label.",
                ([Description("Account id")] string accountId,
                 [Description("Email id")] string emailId,
                 [Description("'archive', 'inbox', 'trash', 'spam', 'drafts', 'sentitems' or a label id")] string destination) =>
                    Record(r, s, "move_email", Args(("emailId", emailId), ("destination", destination)), null, """{"moved":true}""")),

            Tool("mark_email_as_read", "Mark an email read or unread.",
                ([Description("Account id")] string accountId,
                 [Description("Email id")] string emailId,
                 [Description("True for read, false for unread")] bool isRead) =>
                    Record(r, s, "mark_email_as_read", Args(("emailId", emailId), ("isRead", isRead)), null, """{"updated":true}""")),

            Tool("bulk_delete_emails", "Delete up to 50 emails.",
                ([Description("Emails to delete")] EmailRef[] items) =>
                    Record(r, s, "bulk_delete_emails", Args(("items", items)), null, """{"deleted":true}""")),

            Tool("bulk_move_emails", "Move up to 50 emails.",
                ([Description("Emails to move")] EmailRef[] items,
                 [Description("Same values as move_email")] string destination) =>
                    Record(r, s, "bulk_move_emails", Args(("items", items), ("destination", destination)), null, """{"moved":true}""")),

            Tool("bulk_mark_emails_as_read", "Mark up to 50 emails read or unread.",
                ([Description("Emails to update")] EmailRef[] items,
                 [Description("True for read, false for unread")] bool isRead) =>
                    Record(r, s, "bulk_mark_emails_as_read", Args(("items", items), ("isRead", isRead)), null, """{"updated":true}""")),

            Tool("get_unsubscribe_info", "Get the unsubscribe methods an email offers.",
                ([Description("Account id")] string accountId, [Description("Email id")] string emailId) =>
                    Record(r, s, "get_unsubscribe_info", Args(("emailId", emailId)), null, """{"methods":["one-click"]}""")),

            Tool("unsubscribe_from_email", "Unsubscribe from the mailing list an email came from.",
                ([Description("Account id")] string accountId,
                 [Description("Email id")] string emailId,
                 [Description("'auto', 'one-click', 'https' or 'mailto'")] string method = "auto") =>
                    Record(r, s, "unsubscribe_from_email", Args(("emailId", emailId)), null, """{"unsubscribed":true}""")),

            Tool("get_contextual_email_summary", "Summarize recent email clustered by topic, per account.",
                ([Description("Comma-separated topics to focus on")] string? topics = null,
                 [Description("Emails to read per account")] int countPerAccount = 50,
                 [Description("Only unread emails")] bool unreadOnly = false,
                 [Description("Include body previews")] bool includeBodyPreview = false,
                 [Description("Samples per cluster")] int maxSamplesPerCluster = 5) =>
                    Record(r, s, "get_contextual_email_summary", Args(("topics", topics)), null, """{"clusters":[]}""")),

            Tool("get_contacts", "Get contacts for one account or all.",
                ([Description("Account id")] string? accountId = null, [Description("Maximum contacts")] int count = 50) =>
                    Record(r, s, "get_contacts", Args(("accountId", accountId)), null, "[]")),

            Tool("search_contacts", "Search contacts by name, email or company.",
                ([Description("Search text")] string query,
                 [Description("Account id")] string? accountId = null,
                 [Description("Maximum results")] int count = 50) =>
                    Record(r, s, "search_contacts", Args(("query", query)), null,
                        $$"""[{"contactId":"c-9","displayName":"Dana Whitfield","email":"{{DanaAddress}}"}]""")),

            Tool("get_contact_details", "Get a contact's full details.",
                ([Description("Account id")] string accountId, [Description("Contact id")] string contactId) =>
                    Record(r, s, "get_contact_details", Args(("contactId", contactId)), null, """{"displayName":"Dana Whitfield"}""")),

            Tool("create_contact", "Create a contact. email and phone take comma-separated lists.",
                ([Description("Display name")] string displayName,
                 [Description("Account id")] string? accountId = null,
                 [Description("Given name")] string? givenName = null,
                 [Description("Surname")] string? surname = null,
                 [Description("Email addresses, comma-separated")] string? email = null,
                 [Description("Phone numbers, comma-separated")] string? phone = null,
                 [Description("Job title")] string? jobTitle = null,
                 [Description("Company")] string? companyName = null,
                 [Description("Notes")] string? notes = null) =>
                    Record(r, s, "create_contact", Args(("displayName", displayName)), null, """{"contactId":"c-new"}""")),

            Tool("update_contact", "Update a contact; only the fields given change.",
                ([Description("Account id")] string accountId,
                 [Description("Contact id")] string contactId,
                 [Description("Display name")] string? displayName = null,
                 [Description("Email addresses, comma-separated")] string? email = null,
                 [Description("Phone numbers, comma-separated")] string? phone = null,
                 [Description("Notes")] string? notes = null) =>
                    Record(r, s, "update_contact", Args(("contactId", contactId)), null, """{"updated":true}""")),

            Tool("delete_contact", "Delete a contact.",
                ([Description("Account id")] string accountId, [Description("Contact id")] string contactId) =>
                    Record(r, s, "delete_contact", Args(("contactId", contactId)), null, """{"deleted":true}""")),

            Tool("get_guide", "Get a markdown guide: index, overview, accounts, email, calendar, contacts, attachments, scenarios or providers.",
                ([Description("Guide name; omit for the index")] string? name = null) =>
                    Record(r, s, "get_guide", Args(("name", name)), null,
                        "# Guides\n- email: get_emails, search_emails, send_email (to is an array)\n- calendar: get_calendar_events needs timeZone")),
        ];
    }

    // ── OneDrive ──────────────────────────────────────────────────────────────

    private static List<McpServerTool> OneDriveTools(string server, CallRecorder r) =>
    [
        Tool("list_files", "List the files and folders in a folder.",
            ([Description("Folder path, e.g. 'Documents/Reports'; omit for the root")] string? folder_path = null,
             [Description("Maximum items")] int? limit = null) =>
                Record(r, server, "list_files", Args(("folder_path", folder_path), ("limit", limit)),
                    string.IsNullOrWhiteSpace(folder_path) ? "'folder_path' is empty" : null,
                    """[{"name":"Q3-report.docx","size":48213},{"name":"budget.xlsx","size":9021},{"name":"archive","folder":true}]""")),

        Tool("search_files", "Search file names and content.",
            ([Description("Search text")] string query) =>
                Record(r, server, "search_files", Args(("query", query)),
                    string.IsNullOrWhiteSpace(query) ? "'query' is empty" : null, """[{"path":"Documents/Reports/Q3-report.docx"}]""")),

        Tool("get_file_info", "Get a file's metadata.",
            ([Description("File path")] string file_path) =>
                Record(r, server, "get_file_info", Args(("file_path", file_path)), null, """{"size":48213}""")),

        Tool("download_file", "Download a file into a local directory.",
            ([Description("File path")] string file_path,
             [Description("Directory to save into")] string save_directory) =>
                Record(r, server, "download_file", Args(("file_path", file_path), ("save_directory", save_directory)), null, """{"saved":true}""")),
    ];

    // ── todo ──────────────────────────────────────────────────────────────────

    private static List<McpServerTool> TodoTools(CallRecorder r)
    {
        const string s = Todo;
        return
        [
            Tool("add_task", "Add a task. Returns the task as JSON.",
                ([Description("Task title")] string title,
                 [Description("Due date, yyyy-MM-dd")] string due_date,
                 [Description("none, daily, weekly, monthly, quarterly, biannual or yearly")] string recurrence = "none",
                 [Description("Longer description")] string? description = null,
                 [Description("Last date a recurring task recurs, yyyy-MM-dd")] string? recurrence_until = null,
                 [Description("How many times a recurring task recurs")] int? recurrence_count = null) =>
                {
                    var problem = string.IsNullOrWhiteSpace(title) ? "'title' is empty"
                        : !DateOnly.TryParse(due_date, out _) && !DateTime.TryParse(due_date, out _) ? "'due_date' is not a date"
                        : null;
                    return Record(r, s, "add_task",
                        Args(("title", title), ("due_date", due_date), ("recurrence", recurrence), ("description", description)),
                        problem, $$"""{"id":"t-{{Guid.NewGuid():N}}","title":"{{title}}","due_date":"{{due_date}}","status":"active"}""");
                }),

            Tool("list_tasks", "List active tasks.",
                ([Description("Only tasks due before this date")] string? due_before = null,
                 [Description("Only tasks due after this date")] string? due_after = null,
                 [Description("Text filter")] string? query = null,
                 [Description("due_date, created_at or title")] string sort = "due_date",
                 [Description("Shorter output")] bool compact = false) =>
                    Record(r, s, "list_tasks", Args(("query", query)), null, """[{"id":"t-1","title":"File taxes","due_date":"2026-10-15"}]""")),

            Tool("get_task", "Get one task with its notes.",
                ([Description("Task id")] string id) => Record(r, s, "get_task", Args(("id", id)), null, """{"id":"t-1"}""")),

            Tool("add_task_note", "Append a timestamped note to a task.",
                ([Description("Task id")] string id, [Description("Note text")] string text, [Description("Where the note came from")] string? source = null) =>
                    Record(r, s, "add_task_note", Args(("id", id), ("text", text)), null, """{"ok":true}""")),

            Tool("complete_task", "Complete a task; a recurring task spawns its next occurrence.",
                ([Description("Task id")] string id, [Description("Stop a recurring task")] bool stop_recurrence = false) =>
                    Record(r, s, "complete_task", Args(("id", id)), null, """{"ok":true}""")),

            Tool("delete_task", "Soft-delete a task.",
                ([Description("Task id")] string id) => Record(r, s, "delete_task", Args(("id", id)), null, """{"ok":true}""")),

            Tool("restore_task", "Restore a deleted task.",
                ([Description("Task id")] string id) => Record(r, s, "restore_task", Args(("id", id)), null, """{"ok":true}""")),

            Tool("list_deleted", "List deleted tasks that can be restored.",
                () => Record(r, s, "list_deleted", Args(), null, "[]")),

            Tool("uncomplete_task", "Reverse complete_task.",
                ([Description("Task id")] string id) => Record(r, s, "uncomplete_task", Args(("id", id)), null, """{"ok":true}""")),

            Tool("update_task", "Update a task; only the fields given change.",
                ([Description("Task id")] string id,
                 [Description("New title")] string? title = null,
                 [Description("New description")] string? description = null,
                 [Description("New due date")] string? due_date = null) =>
                    Record(r, s, "update_task", Args(("id", id)), null, """{"ok":true}""")),

            Tool("list_completed", "List completed tasks.",
                ([Description("Only tasks completed after this date")] string? completed_after = null,
                 [Description("Text filter")] string? query = null) =>
                    Record(r, s, "list_completed", Args(("query", query)), null, "[]")),
        ];
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static McpServerTool Tool(string name, string description, Delegate method) =>
        McpServerTool.Create(method, new McpServerToolCreateOptions { Name = name, Description = description });

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static CallToolResult Record(
        CallRecorder recorder, string server, string tool, Dictionary<string, object?> args, string? problem, string okText)
    {
        recorder.Add(new RecordedCall(server, tool, args, problem is null, problem));
        return problem is null
            ? new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = okText }] }
            : new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"Invalid arguments: {problem}." }] };
    }
}
