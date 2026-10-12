using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.ElicitationFixture.Tools;

/// <summary>
/// Tools that ask the caller a question mid-call, one per form shape, so elicitation hand-back
/// and the chat's question form can be exercised end to end.
/// </summary>
/// <remarks>
/// <para>
/// Every tool asks the way a well-behaved server does under MRTR: it throws
/// <see cref="InputRequiredException"/> with its question and a round counter as the request
/// state, holds nothing, and reads the answer from the retried call's input responses. That is
/// the shape <c>design/mcp-elicitation-handback.md</c> is built for.
/// </para>
/// <para>
/// No live server RockBot talks to elicits, so without this the hand-back path and the form were
/// only verifiable in tests. The server holds no state, reads no credentials and makes no
/// outbound calls; every result just echoes what was answered.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ElicitationFixtureTools
{
    private const string Key = "answer";

    [McpServerTool(Name = "pick_meaning"), Description(
        "Looks up a word with several meanings. Asks which meaning you want (a single choice).")]
    public static string PickMeaning(
        RequestContext<CallToolRequestParams> context,
        [Description("The word to look up, e.g. Mercury.")] string term)
    {
        if (Answer(context) is not { } answer)
        {
            throw Ask(context, $"Which {term} do you mean?", new()
            {
                ["meaning"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                {
                    Title = "Meaning",
                    OneOf =
                    [
                        new() { Const = "planet", Title = "The planet" },
                        new() { Const = "element", Title = "The chemical element" },
                        new() { Const = "band", Title = "The band" },
                        new() { Const = "god", Title = "The Roman god" },
                        new() { Const = "car", Title = "The car brand" },
                    ],
                },
            }, required: ["meaning"]);
        }

        return answer.Action == "accept"
            ? $"Looked up {term}: the {Text(answer, "meaning")}."
            : $"No meaning was chosen ({answer.Action}); returning every sense of {term}.";
    }

    [McpServerTool(Name = "confirm_overwrite"), Description(
        "Overwrites rows in a table. Asks for a yes/no confirmation first (a decision only the user may make).")]
    public static string ConfirmOverwrite(
        RequestContext<CallToolRequestParams> context,
        [Description("How many rows to overwrite.")] int rows = 12)
    {
        if (Answer(context) is not { } answer)
        {
            throw Ask(context, $"Overwrite {rows} rows? This can't be undone.", new()
            {
                ["confirm"] = new ElicitRequestParams.BooleanSchema
                {
                    Title = "Overwrite them?",
                    Description = "Yes replaces the rows; No leaves them alone.",
                },
            }, required: ["confirm"]);
        }

        if (answer.Action != "accept")
            return $"Cancelled ({answer.Action}); no rows were touched.";

        return Flag(answer, "confirm") == true
            ? $"Overwrote {rows} rows."
            : $"Left the {rows} rows alone.";
    }

    [McpServerTool(Name = "book_table"), Description(
        "Books a restaurant table. Asks for the party size, date and an optional note (numbers, a date, free text).")]
    public static string BookTable(
        RequestContext<CallToolRequestParams> context,
        [Description("The restaurant's name.")] string restaurant)
    {
        if (Answer(context) is not { } answer)
        {
            // The note's title and description carry markup on purpose: a client must show it
            // literally, never render it.
            throw Ask(context, $"Booking a table at {restaurant}. A few details, please.", new()
            {
                ["party_size"] = new ElicitRequestParams.NumberSchema
                {
                    Type = "integer",
                    Title = "Party size",
                    Minimum = 1,
                    Maximum = 12,
                },
                ["date"] = new ElicitRequestParams.StringSchema
                {
                    Title = "Date",
                    Format = "date",
                },
                ["note"] = new ElicitRequestParams.StringSchema
                {
                    Title = "Note for the <b>kitchen</b>",
                    Description = "**Allergies**, a birthday, a [link](https://example.com)? Shown as typed.",
                    MaxLength = 200,
                },
            }, required: ["party_size", "date"]);
        }

        if (answer.Action != "accept")
            return $"No table booked at {restaurant} ({answer.Action}).";

        var note = Text(answer, "note");
        return $"Booked a table for {Number(answer, "party_size")} at {restaurant} on {Text(answer, "date")}" +
               (string.IsNullOrEmpty(note) ? "." : $", with the note \"{note}\".");
    }

    [McpServerTool(Name = "choose_toppings"), Description(
        "Orders a pizza. Asks which toppings (pick one to three) and an optional receipt email.")]
    public static string ChooseToppings(RequestContext<CallToolRequestParams> context)
    {
        if (Answer(context) is not { } answer)
        {
            throw Ask(context, "Which toppings would you like?", new()
            {
                ["toppings"] = new ElicitRequestParams.TitledMultiSelectEnumSchema
                {
                    Title = "Toppings",
                    MinItems = 1,
                    MaxItems = 3,
                    Items = new ElicitRequestParams.TitledEnumItemsSchema
                    {
                        AnyOf =
                        [
                            new() { Const = "mushroom", Title = "Mushroom" },
                            new() { Const = "pepperoni", Title = "Pepperoni" },
                            new() { Const = "olive", Title = "Olive" },
                            new() { Const = "pineapple", Title = "Pineapple" },
                            new() { Const = "basil", Title = "Basil" },
                        ],
                    },
                },
                ["email"] = new ElicitRequestParams.StringSchema
                {
                    Title = "Email for the receipt",
                    Format = "email",
                },
            }, required: ["toppings"]);
        }

        if (answer.Action != "accept")
            return $"No pizza ordered ({answer.Action}).";

        var toppings = answer.Content?.TryGetValue("toppings", out var t) == true && t.ValueKind == JsonValueKind.Array
            ? string.Join(", ", t.EnumerateArray().Select(v => v.GetString()))
            : "(none)";
        var email = Text(answer, "email");
        return $"Ordered a pizza with {toppings}" + (string.IsNullOrEmpty(email) ? "." : $"; the receipt goes to {email}.");
    }

    [McpServerTool(Name = "two_step"), Description(
        "Creates a project in two questions: first its name, then a yes/no confirmation.")]
    public static string TwoStep(RequestContext<CallToolRequestParams> context)
    {
        var round = Round(context);
        if (round == 0)
        {
            throw Ask(context, "What should the new project be called?", new()
            {
                ["name"] = new ElicitRequestParams.StringSchema { Title = "Project name", MinLength = 3, MaxLength = 40 },
            }, required: ["name"]);
        }

        var answer = Answer(context)!;
        if (answer.Action != "accept")
            return $"No project created ({answer.Action} at question {round}).";

        if (round == 1)
        {
            var name = Text(answer, "name");
            throw Ask(context, $"Create the project \"{name}\" now?", new()
            {
                ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = "Create it?" },
            }, required: ["confirm"], state: $"2|{name}");
        }

        var projectName = context.Params?.RequestState?.Split('|', 2) is [_, var n] ? n : "(unnamed)";
        return Flag(answer, "confirm") == true
            ? $"Created the project \"{projectName}\"."
            : $"Didn't create the project \"{projectName}\".";
    }

    [McpServerTool(Name = "ping"), Description("Answers pong without asking anything; checks the server is reachable.")]
    public static string Ping() => "pong";

    // ── MRTR plumbing ────────────────────────────────────────────────────────

    /// <summary>Which question this call is on: 0 for the first call, then the request state's round.</summary>
    private static int Round(RequestContext<CallToolRequestParams> context) =>
        context.Params?.RequestState is { } state
        && int.TryParse(state.Split('|')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var round)
            ? round
            : 0;

    /// <summary>The answer to the question this tool asked, or null on the first call.</summary>
    private static ElicitResult? Answer(RequestContext<CallToolRequestParams> context) =>
        context.Params?.InputResponses?.TryGetValue(Key, out var response) == true
            ? response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)
            : null;

    private static InputRequiredException Ask(
        RequestContext<CallToolRequestParams> context,
        string message,
        Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition> properties,
        IList<string> required,
        string? state = null) =>
        new(
            new Dictionary<string, InputRequest>
            {
                [Key] = InputRequest.ForElicitation(new ElicitRequestParams
                {
                    Message = message,
                    RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = properties, Required = required },
                }),
            },
            state ?? (Round(context) + 1).ToString(CultureInfo.InvariantCulture));

    private static string Text(ElicitResult answer, string field) =>
        answer.Content?.TryGetValue(field, out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool? Flag(ElicitResult answer, string field) =>
        answer.Content?.TryGetValue(field, out var value) == true
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static string Number(ElicitResult answer, string field) =>
        answer.Content?.TryGetValue(field, out var value) == true ? value.GetRawText() : "?";
}
