namespace RockBot.Llm;

/// <summary>
/// Resolves <see cref="ModelBehavior"/> by matching a model ID against configured overrides
/// and loading prompt strings from files in <see cref="ModelBehaviorOptions.BasePath"/>.
///
/// Resolution order for each string property (e.g. AdditionalSystemPrompt):
///   1. File in <c>{BasePath}/{matched-prefix}/{property-filename}.md</c> — highest priority,
///      allows users to customise per-model prompts on the PVC without rebuilding the image.
///   2. Inline value from <see cref="ModelBehaviorEntry"/> (config / appsettings.json).
///   3. Null / default — no content injected.
///
/// Boolean and numeric properties are always read from config; they have no file equivalent.
/// </summary>
internal sealed class DefaultModelBehaviorProvider(ModelBehaviorOptions options) : IModelBehaviorProvider
{
    // Maps ModelBehavior string property names to their on-disk filenames.
    private const string AdditionalSystemPromptFile = "additional-system-prompt.md";
    private const string PreToolLoopPromptFile = "pre-tool-loop-prompt.md";

    public ModelBehavior GetBehavior(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId) && options.Models.Count == 0)
            return ModelBehavior.Default;

        var entry = FindEntry(modelId);
        var modelDir = FindModelDirectory(modelId);

        return new ModelBehavior
        {
            NudgeOnHallucinatedToolCalls = entry?.NudgeOnHallucinatedToolCalls ?? false,
            RequireExplicitConfirmationForDestructiveTools =
                entry?.RequireExplicitConfirmationForDestructiveTools ?? false,
            MaxToolIterationsOverride = entry?.MaxToolIterationsOverride,
            ScheduledTaskResultMode = entry?.ScheduledTaskResultMode ?? ScheduledTaskResultMode.Summarize,

            // String properties: file takes priority over config value
            AdditionalSystemPrompt =
                ReadFile(modelDir, AdditionalSystemPromptFile) ?? entry?.AdditionalSystemPrompt,
            PreToolLoopPrompt =
                ReadFile(modelDir, PreToolLoopPromptFile) ?? entry?.PreToolLoopPrompt,
        };
    }

    // ── Config lookup ─────────────────────────────────────────────────────────

    private ModelBehaviorEntry? FindEntry(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId) || options.Models.Count == 0)
            return null;

        if (options.Models.TryGetValue(modelId, out var exact))
            return exact;

        return options.Models
            .Where(kvp => modelId.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kvp => kvp.Key.Length)
            .Select(kvp => (ModelBehaviorEntry?)kvp.Value)
            .FirstOrDefault();
    }

    // ── File lookup ───────────────────────────────────────────────────────────

    private string? FindModelDirectory(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return null;

        var basePath = options.BasePath;
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(AppContext.BaseDirectory, basePath);

        if (!Directory.Exists(basePath))
            return null;

        try
        {
            // Find the subdirectory whose name is the longest prefix of modelId
            return Directory.GetDirectories(basePath)
                .Where(d => modelId.StartsWith(
                    Path.GetFileName(d), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => Path.GetFileName(d).Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadFile(string? directory, string filename)
    {
        if (directory is null) return null;
        var path = Path.Combine(directory, filename);
        if (!File.Exists(path)) return null;
        try
        {
            var content = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }
}
