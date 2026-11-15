using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Writes <see cref="LlmConfigSnapshot"/> to <c>{BasePath}/llm-config.json</c> once at startup,
/// for the introspection sidecar's <c>get_llm_config</c> tool.
/// </summary>
/// <remarks>
/// A failed write is logged and swallowed. Not being able to describe its models must never
/// stop the agent from starting.
/// </remarks>
public sealed class LlmConfigSnapshotWriter(
    LlmConfigSnapshot snapshot,
    IOptions<AgentProfileOptions> profileOptions,
    ILogger<LlmConfigSnapshotWriter> logger) : IHostedService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string FilePath { get; } = ResolvePath(profileOptions.Value.BasePath);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);

            // Write-then-rename so the sidecar never reads a half-written file.
            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(
                temp, JsonSerializer.Serialize(snapshot, WriteOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);

            logger.LogInformation("LLM configuration snapshot written to {Path}", FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the LLM configuration snapshot to {Path}", FilePath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string ResolvePath(string basePath)
    {
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(AppContext.BaseDirectory, basePath);
        return Path.Combine(basePath, LlmConfigFiles.SnapshotFile);
    }
}
