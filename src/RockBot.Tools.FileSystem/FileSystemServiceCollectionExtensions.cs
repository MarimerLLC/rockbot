using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Tools.FileSystem;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// DI registration extensions for shared-volume file tools.
/// </summary>
public static class FileSystemServiceCollectionExtensions
{
    /// <summary>
    /// Registers file tools for reading, writing, listing, and deleting files on the shared volume.
    /// </summary>
    public static AgentHostBuilder AddFileSystemTools(
        this AgentHostBuilder builder,
        Action<FileSystemOptions> configure)
    {
        var options = new FileSystemOptions();
        configure(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton<IToolSkillProvider, FileSystemToolSkillProvider>();
        builder.Services.AddHostedService<FileSystemToolRegistrar>();

        return builder;
    }
}
