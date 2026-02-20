using System.Text;

namespace RockBot.Host;

/// <summary>
/// Builds a system prompt by prepending the agent's name and appending
/// each profile document's raw content in order. The result is cached
/// after the first call since profile documents and identity are immutable.
/// </summary>
public sealed class DefaultSystemPromptBuilder : ISystemPromptBuilder
{
    private string? _cached;

    /// <inheritdoc />
    public string Build(AgentProfile profile, AgentIdentity identity)
    {
        if (_cached is not null)
            return _cached;

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

        _cached = sb.ToString().TrimEnd();
        return _cached;
    }
}
