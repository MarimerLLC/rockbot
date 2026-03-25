using System.Text;

namespace RockBot.Host;

/// <summary>
/// Builds a system prompt by prepending the agent's name and appending
/// each profile document's raw content in order. Caches the result and
/// rebuilds automatically when the profile version or agent name changes.
/// </summary>
public sealed class DefaultSystemPromptBuilder(
    ProfileHolder profileHolder,
    AgentNameHolder agentNameHolder) : ISystemPromptBuilder
{
    private string? _cached;
    private long _cachedProfileVersion = -1;
    private long _cachedNameVersion = -1;

    /// <inheritdoc />
    public string Build(AgentProfile profile, AgentIdentity identity)
    {
        var currentProfileVersion = profileHolder.Version;
        var currentNameVersion = agentNameHolder.Version;
        if (_cached is not null
            && _cachedProfileVersion == currentProfileVersion
            && _cachedNameVersion == currentNameVersion)
            return _cached;

        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(identity);

        var displayName = agentNameHolder.DisplayName ?? identity.Name;

        var sb = new StringBuilder();
        sb.Append("You are ");
        sb.Append(displayName);
        sb.AppendLine(".");
        sb.AppendLine();

        foreach (var doc in profile.Documents)
        {
            sb.AppendLine(doc.RawContent.TrimEnd());
            sb.AppendLine();
        }

        _cached = sb.ToString().TrimEnd();
        _cachedProfileVersion = currentProfileVersion;
        _cachedNameVersion = currentNameVersion;
        return _cached;
    }
}
