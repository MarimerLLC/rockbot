using System.Text;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Turns the elicitations that happened during a tool call into a note appended to the tool's
/// result, so the agent learns that the server asked something and how it was answered.
/// </summary>
/// <remarks>
/// Without this the agent sees only a thin or empty result and has no idea a question was asked
/// and declined — it would retry the same call and get the same non-answer. The note names the
/// missing fields so the next attempt can carry them as ordinary tool arguments, or the agent
/// can put the question to the user.
/// </remarks>
public static class McpElicitationNote
{
    /// <summary>
    /// Builds the note, or returns null when there is nothing worth telling the agent.
    /// </summary>
    /// <param name="records">What was asked during the call.</param>
    /// <param name="toolParameters">
    /// Parameter names from the called tool's input schema, or null when the schema is not known.
    /// Used to tell apart a declined field the agent can supply on retry from one it cannot:
    /// retrying with a field the tool does not take just provokes the same question.
    /// </param>
    public static string? Build(
        IReadOnlyList<McpElicitationRecord> records,
        IReadOnlyCollection<string>? toolParameters = null)
    {
        if (records.Count == 0)
            return null;

        var declined = records.Where(r => !r.IsAccepted).ToList();

        var builder = new StringBuilder();
        builder.Append("[rockbot] The MCP server asked ")
            .Append(records.Count == 1 ? "a question" : $"{records.Count} questions")
            .AppendLine(" while this tool was running:");

        foreach (var record in records)
        {
            builder.Append("- \"")
                .Append(McpElicitationSchemaDescriber.Flatten(record.Message))
                .Append('"');

            if (record.RequestedFields.Count > 0)
                builder.Append(" (fields: ").Append(Names(record.RequestedFields, ", ")).Append(')');

            builder.Append(" — answered '").Append(record.Action).Append('\'');

            if (!string.IsNullOrWhiteSpace(record.Reason))
                builder.Append(": ").Append(McpElicitationSchemaDescriber.Flatten(record.Reason));

            builder.AppendLine();
        }

        if (declined.Count > 0)
        {
            var fields = DeclinedFields(declined);
            builder.Append("The server was told the question could not be answered, so this result may be incomplete.");

            if (toolParameters is null || fields.Count == 0)
            {
                builder.Append(" If you need the full answer, supply ")
                    .Append(fields.Count > 0 ? Names(fields, " / ") : "the missing information")
                    .Append(" in the tool arguments, or ask the user for it, and call the tool again.");
            }
            else
            {
                var (arguments, others) = Split(fields, toolParameters);

                if (arguments.Count > 0)
                {
                    builder.Append(" If you need the full answer, supply ")
                        .Append(Names(arguments, " / "))
                        .Append(" in the tool arguments and call the tool again.");
                }

                if (others.Count > 0)
                {
                    builder.Append(' ').Append(NotParameters(others))
                        .Append(", so calling it again will only get the same question: ask the user for ")
                        .Append(others.Count == 1 ? "it" : "them")
                        .Append(", or work with this result as it is.");
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Describes questions declined during a call that then timed out or failed, for appending
    /// to the error. Empty when nothing was declined.
    /// </summary>
    /// <remarks>
    /// A call that times out or fails just after a declined elicitation has almost certainly
    /// done so <em>because</em> of it: the server is waiting on, or gave up without, information
    /// it is never going to get. Saying so turns a "transient, retry me" error into something
    /// the agent can act on.
    /// </remarks>
    /// <param name="records">What was asked during the call.</param>
    /// <param name="toolParameters">As for <see cref="Build"/>.</param>
    public static string DescribeDeclined(
        IReadOnlyList<McpElicitationRecord> records,
        IReadOnlyCollection<string>? toolParameters = null)
    {
        var declined = records.Where(r => !r.IsAccepted).ToList();
        if (declined.Count == 0)
            return string.Empty;

        var fields = DeclinedFields(declined);
        var builder = new StringBuilder()
            .Append(" The server also asked ").Append(declined.Count)
            .Append(" question(s) mid-call that this client declined.");

        if (fields.Count > 0)
            builder.Append(" It asked for ").Append(Names(fields, ", ")).Append('.');

        if (toolParameters is null || fields.Count == 0)
        {
            builder.Append(" Supplying that information in the tool arguments may let the call complete.");
            return builder.ToString();
        }

        var (arguments, others) = Split(fields, toolParameters);

        if (arguments.Count > 0)
        {
            builder.Append(" Supplying ").Append(Names(arguments, " / "))
                .Append(" in the tool arguments may let the call complete.");
        }

        if (others.Count > 0)
        {
            builder.Append(' ').Append(NotParameters(others))
                .Append(", so a retry will stall on the same question: ask the user instead.");
        }

        return builder.ToString();
    }

    private static List<string> DeclinedFields(IEnumerable<McpElicitationRecord> declined)
        => [.. declined.SelectMany(r => r.RequestedFields).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Splits declined fields into those the tool takes as arguments and those it does not.
    /// Case-insensitive, like the rest of the bridge's field matching.
    /// </summary>
    private static (List<string> Arguments, List<string> Others) Split(
        List<string> fields, IReadOnlyCollection<string> toolParameters)
    {
        var parameters = new HashSet<string>(toolParameters, StringComparer.OrdinalIgnoreCase);
        return ([.. fields.Where(parameters.Contains)], [.. fields.Where(f => !parameters.Contains(f))]);
    }

    private static string NotParameters(List<string> fields)
        => fields.Count == 1
            ? $"{Names(fields, "")} is not a parameter of this tool"
            : $"{Names(fields, " / ")} are not parameters of this tool";

    /// <summary>
    /// Joins server-authored field names for display, flattened so a name cannot forge its own
    /// lines in the note.
    /// </summary>
    private static string Names(IEnumerable<string> fields, string separator)
        => string.Join(separator, fields.Select(McpElicitationSchemaDescriber.Flatten));

    /// <summary>
    /// Appends the note to a tool result's content blocks as an extra text block, leaving the
    /// server's own blocks untouched.
    /// </summary>
    public static IReadOnlyList<ToolContentBlock> AppendTo(IReadOnlyList<ToolContentBlock>? blocks, string note)
    {
        var combined = new List<ToolContentBlock>((blocks?.Count ?? 0) + 1);
        if (blocks is not null)
            combined.AddRange(blocks);
        combined.Add(new ToolContentBlock { Type = "text", Text = note });
        return combined;
    }
}
