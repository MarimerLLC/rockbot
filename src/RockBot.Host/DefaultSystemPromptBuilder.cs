using System.Text;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Builds a system prompt by prepending the agent's name and appending
/// each profile document's raw content in order. Caches the result and
/// rebuilds automatically when the profile version, agent name, category,
/// or category hint file changes.
/// </summary>
public sealed class DefaultSystemPromptBuilder(
    ProfileHolder profileHolder,
    AgentNameHolder agentNameHolder,
    IOptions<AgentProfileOptions> profileOptions) : ISystemPromptBuilder
{
    private readonly string _profileBasePath = profileOptions.Value.BasePath;

    private string? _cached;
    private long _cachedProfileVersion = -1;
    private long _cachedNameVersion = -1;
    private string? _cachedCategory;
    private DateTime _cachedHintMtime;

    /// <inheritdoc />
    public string Build(AgentProfile profile, AgentIdentity identity) =>
        Build(profile, identity, category: null);

    /// <inheritdoc />
    public string Build(AgentProfile profile, AgentIdentity identity, string? category)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(identity);

        var currentProfileVersion = profileHolder.Version;
        var currentNameVersion = agentNameHolder.Version;
        var hintPath = ResolveHintPath(category);
        var currentHintMtime = hintPath is not null && File.Exists(hintPath)
            ? File.GetLastWriteTimeUtc(hintPath)
            : DateTime.MinValue;

        if (_cached is not null
            && _cachedProfileVersion == currentProfileVersion
            && _cachedNameVersion == currentNameVersion
            && string.Equals(_cachedCategory, category, StringComparison.Ordinal)
            && _cachedHintMtime == currentHintMtime)
        {
            return _cached;
        }

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

        if (hintPath is not null && File.Exists(hintPath))
        {
            var hintBody = File.ReadAllText(hintPath).TrimEnd();
            if (hintBody.Length > 0)
            {
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine(hintBody);
                sb.AppendLine();
            }
        }

        _cached = sb.ToString().TrimEnd();
        _cachedProfileVersion = currentProfileVersion;
        _cachedNameVersion = currentNameVersion;
        _cachedCategory = category;
        _cachedHintMtime = currentHintMtime;
        return _cached;
    }

    private string? ResolveHintPath(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return null;

        if (!IsSafeFileName(category))
            return null;

        var baseDir = Path.IsPathRooted(_profileBasePath)
            ? _profileBasePath
            : Path.Combine(AppContext.BaseDirectory, _profileBasePath);

        return Path.Combine(baseDir, "prompt-hints", category + ".md");
    }

    private static bool IsSafeFileName(string s)
    {
        if (s.Contains('/') || s.Contains('\\') || s.Contains("..")) return false;
        foreach (var c in Path.GetInvalidFileNameChars())
            if (s.Contains(c)) return false;
        return true;
    }
}
