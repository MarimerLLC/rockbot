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
    public static string? Build(IReadOnlyList<McpElicitationRecord> records)
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
                builder.Append(" (fields: ").Append(string.Join(", ", record.RequestedFields)).Append(')');

            builder.Append(" — answered '").Append(record.Action).Append('\'');

            if (!string.IsNullOrWhiteSpace(record.Reason))
                builder.Append(": ").Append(McpElicitationSchemaDescriber.Flatten(record.Reason));

            builder.AppendLine();
        }

        if (declined.Count > 0)
        {
            var fields = declined
                .SelectMany(r => r.RequestedFields)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            builder.Append("The server was told the question could not be answered, so this result may be ")
                .Append("incomplete. If you need the full answer, supply ")
                .Append(fields.Count > 0 ? string.Join(" / ", fields) : "the missing information")
                .AppendLine(" in the tool arguments, or ask the user for it, and call the tool again.");
        }

        return builder.ToString().TrimEnd();
    }

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
