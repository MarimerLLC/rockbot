using System.Reflection;

namespace RockBot.UserProxy;

/// <summary>
/// Reads the informational version from the entry assembly.
/// All RockBot assemblies share the same version via Directory.Build.props.
/// </summary>
public static class AssemblyVersion
{
    public static string Current { get; } =
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";
}
