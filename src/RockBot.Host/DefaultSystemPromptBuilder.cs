using System.Text;

namespace RockBot.Host;

/// <summary>
/// Builds a system prompt by prepending the agent's name and appending
/// each profile document's raw content in order. Caches the result and
/// rebuilds automatically when the profile version changes.
/// </summary>
public sealed class DefaultSystemPromptBuilder(ProfileHolder profileHolder) : ISystemPromptBuilder
{
    private string? _cached;
    private long _cachedVersion = -1;

    /// <inheritdoc />
    public string Build(AgentProfile profile, AgentIdentity identity)
    {
        var currentVersion = profileHolder.Version;
        if (_cached is not null && _cachedVersion == currentVersion)
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
        _cachedVersion = currentVersion;
        return _cached;
    }
}
