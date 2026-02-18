using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Loads agent profile documents from disk, resolving relative paths against
/// <see cref="AppContext.BaseDirectory"/> (the output directory where content files
/// are copied) combined with <see cref="AgentProfileOptions.BasePath"/>.
/// </summary>
internal sealed class FileAgentProfileProvider(
    IOptions<AgentProfileOptions> options,
    ILogger<FileAgentProfileProvider> logger) : IAgentProfileProvider
{
    public async Task<AgentProfile> LoadAsync(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;

        var soul = await LoadDocumentAsync("soul", opts.SoulPath, opts.BasePath, required: true, cancellationToken);
        var directives = await LoadDocumentAsync("directives", opts.DirectivesPath, opts.BasePath, required: true, cancellationToken);

        AgentProfileDocument? style = null;
        if (opts.StylePath is not null)
        {
            style = await LoadDocumentAsync("style", opts.StylePath, opts.BasePath, required: false, cancellationToken);
        }

        AgentProfileDocument? memoryRules = null;
        if (opts.MemoryRulesPath is not null)
        {
            memoryRules = await LoadDocumentAsync("memory-rules", opts.MemoryRulesPath, opts.BasePath, required: false, cancellationToken);
        }

        var profile = new AgentProfile(soul!, directives!, style, memoryRules);
        logger.LogInformation(
            "Loaded agent profile: soul={SoulSections} sections, directives={DirectivesSections} sections, style={HasStyle}",
            soul!.Sections.Count, directives!.Sections.Count, style is not null);

        return profile;
    }

    private async Task<AgentProfileDocument?> LoadDocumentAsync(
        string documentType, string path, string basePath, bool required, CancellationToken cancellationToken)
    {
        var resolvedPath = ResolvePath(path, basePath);

        if (!File.Exists(resolvedPath))
        {
            if (required)
            {
                throw new FileNotFoundException(
                    $"Required agent profile document '{documentType}' not found at: {resolvedPath}",
                    resolvedPath);
            }

            logger.LogDebug("Optional profile document '{DocumentType}' not found at {Path}, skipping",
                documentType, resolvedPath);
            return null;
        }

        var content = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        logger.LogDebug("Loaded profile document '{DocumentType}' from {Path} ({Length} chars)",
            documentType, resolvedPath, content.Length);

        return ProfileMarkdownParser.Parse(documentType, content);
    }

    private static string ResolvePath(string path, string basePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(AppContext.BaseDirectory, basePath);

        return Path.Combine(baseDir, path);
    }
}
