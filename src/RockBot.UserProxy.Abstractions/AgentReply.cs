using System.Reflection;

namespace RockBot.UserProxy;

/// <summary>
/// Reply from an agent back to the user.
/// </summary>
public sealed record AgentReply
{
    public required string Content { get; init; }
    public required string SessionId { get; init; }
    public required string AgentName { get; init; }
    public bool IsFinal { get; init; } = true;
    public string? StructuredData { get; init; }
    public string? ContentType { get; init; }
    public string? AgentVersion { get; init; }

    /// <summary>
    /// Assembly informational version of the running process.
    /// All RockBot assemblies share the same version via Directory.Build.props.
    /// </summary>
    public static string CurrentVersion { get; } =
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";
}
