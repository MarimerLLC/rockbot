using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Hands a file on the shared volume to a vision-capable model as real multimodal content and
/// returns only the model's textual answer.
/// </summary>
/// <remarks>
/// This is a side call, not a richer tool result, and that is deliberate: on OpenAI-compatible
/// APIs — which is every provider RockBot talks to — tool-role messages accept text only, so
/// bytes can enter a conversation only as content parts on a user message. Running the analysis
/// as its own request sidesteps that entirely and keeps the agent's own context free of bytes:
/// the tool takes a path and returns prose. See <c>design/multimodal-input.md</c>.
/// </remarks>
internal sealed class AnalyzeFileToolExecutor(
    FileSystemOptions options,
    ILlmClient llmClient,
    IReadOnlyCollection<ModelTier> visionTiers,
    ILogger logger) : IToolExecutor
{
    /// <summary>
    /// Steers the analysis model towards describing what is actually in the file. The caller is
    /// another model that cannot see the bytes, so an invented detail here is indistinguishable
    /// from an observed one downstream.
    /// </summary>
    private const string SystemPrompt =
        "You are analysing a file on behalf of another agent that cannot see it. Answer only " +
        "from what is actually present in the file. State plainly when something asked for is " +
        "not visible or not legible rather than inferring it.";

    /// <summary>
    /// Order tried when the requested tier cannot see: strongest first, because an analysis
    /// that has to be re-run is more expensive than the tier difference.
    /// </summary>
    private static readonly ModelTier[] FallbackOrder =
        [ModelTier.High, ModelTier.Balanced, ModelTier.Low];

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg"
    };

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement)
                || pathElement.GetString() is not { Length: > 0 } relativePath)
                return Error(request, "Missing required argument: path");

            if (!args.TryGetValue("prompt", out var promptElement)
                || promptElement.GetString() is not { Length: > 0 } prompt)
                return Error(request, "Missing required argument: prompt");

            var fullPath = FileWriteToolExecutor.SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Error(request, "Invalid path: must be within the shared volume.");

            if (!File.Exists(fullPath))
                return Error(request, $"File not found: {relativePath}");

            var extension = Path.GetExtension(fullPath);
            if (!MimeByExtension.TryGetValue(extension, out var mime))
            {
                return Error(request,
                    $"Cannot analyse '{relativePath}': unrecognised file type '{extension}'. " +
                    $"Types this agent can analyse: {string.Join(", ", options.AnalyzeFileMimeTypes)}.");
            }

            if (!options.AnalyzeFileMimeTypes.Contains(mime, StringComparer.OrdinalIgnoreCase))
            {
                return Error(request,
                    $"Cannot analyse '{relativePath}': {mime} files are not enabled for this agent. " +
                    $"Types this agent can analyse: {string.Join(", ", options.AnalyzeFileMimeTypes)}.");
            }

            var length = new FileInfo(fullPath).Length;
            if (length > options.AnalyzeFileMaxBytes)
            {
                return Error(request,
                    $"Cannot analyse '{relativePath}': {length / 1024} KB exceeds the " +
                    $"{options.AnalyzeFileMaxBytes / 1024} KB limit for file analysis.");
            }

            var tier = ResolveTier(args);
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User,
                [
                    new TextContent(prompt),
                    new DataContent(bytes, mime)
                ])
            };

            logger.LogInformation(
                "analyze_file: {Path} ({Mime}, {Bytes} bytes) on {Tier} tier",
                relativePath, mime, length, tier);

            var response = await llmClient.GetResponseAsync(messages, tier, options: null, ct);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return Error(request,
                    $"The model returned no text for '{relativePath}'. The file may be unreadable " +
                    "to it, or the request may have been filtered.");
            }

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = text,
                IsError = false
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "analyze_file failed");
            return Error(request, $"Analysis failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Honours the requested tier when it can see, and otherwise substitutes the nearest tier
    /// that can. Sending the request to a blind tier anyway would not merely fail — <see
    /// cref="ILlmClient"/> retries a failed Low/High call on Balanced, so a blind Balanced tier
    /// would swallow the second attempt too and report a provider error instead of the real
    /// cause.
    /// </summary>
    private ModelTier ResolveTier(Dictionary<string, JsonElement> args)
    {
        var requested = ModelTier.Balanced;
        if (args.TryGetValue("tier", out var tierElement)
            && tierElement.ValueKind == JsonValueKind.String
            && Enum.TryParse<ModelTier>(tierElement.GetString(), ignoreCase: true, out var parsed))
        {
            requested = parsed;
        }

        if (visionTiers.Contains(requested))
            return requested;

        var substitute = FallbackOrder.First(visionTiers.Contains);
        logger.LogInformation(
            "analyze_file: {Requested} tier does not accept image input — using {Substitute}",
            requested, substitute);
        return substitute;
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) =>
        new()
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = message,
            IsError = true
        };

    private static Dictionary<string, JsonElement> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
    }
}
