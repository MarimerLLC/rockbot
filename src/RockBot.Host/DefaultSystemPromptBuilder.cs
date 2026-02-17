using System.Text;

namespace RockBot.Host;

/// <summary>
/// Builds a system prompt by prepending the agent's name and appending
/// each profile document's raw content in order.
/// </summary>
public sealed class DefaultSystemPromptBuilder : ISystemPromptBuilder
{
    /// <inheritdoc />
    public string Build(AgentProfile profile, AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(identity);

        var sb = new StringBuilder();
        sb.Append("You are ");
        sb.Append(identity.Name);
        sb.AppendLine(".");
        sb.AppendLine();

        foreach (var doc in profile.Documents)
        {
            sb.AppendLine(doc.RawContent.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
