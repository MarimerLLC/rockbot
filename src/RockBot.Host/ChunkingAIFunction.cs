using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Decorator that wraps any <see cref="AIFunction"/> and applies working-memory chunking
/// to large results before they enter the chat history. When a tool result exceeds the
/// configured threshold, it is split into chunks stored in working memory and replaced
/// with an index table pointing to those chunks.
/// </summary>
public sealed class ChunkingAIFunction(
    AIFunction inner,
    IWorkingMemory workingMemory,
    string? sessionId,
    int chunkingThreshold,
    ILogger logger) : AIFunction
{
    private const int ToolResultChunkMaxLength = 20_000;
    private static readonly TimeSpan ToolResultChunkTtl = TimeSpan.FromMinutes(20);

    private static readonly HashSet<string> ChunkingExemptTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetFromWorkingMemory",
        "SearchWorkingMemory",
        "ListWorkingMemory",
    };

    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override JsonElement JsonSchema => inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var result = await inner.InvokeAsync(arguments, cancellationToken);
        var resultStr = result?.ToString() ?? string.Empty;

        if (ChunkingExemptTools.Contains(inner.Name))
            return result;

        if (resultStr.Length <= chunkingThreshold)
            return result;

        return await ChunkResultAsync(inner.Name, resultStr, cancellationToken);
    }

    private async Task<string> ChunkResultAsync(string toolName, string result, CancellationToken ct)
    {
        if (sessionId is not null)
        {
            var chunks = ContentChunker.Chunk(result, ToolResultChunkMaxLength);
            var sanitizedName = SanitizeKeySegment(toolName);
            var runId = Guid.NewGuid().ToString("N")[..8];
            var keyBase = $"tool:{sanitizedName}:{runId}";

            var index = new StringBuilder();
            index.AppendLine(
                $"Tool result for '{toolName}' is large ({result.Length:N0} chars) and has been " +
                $"split into {chunks.Count} chunk(s) stored in working memory.");
            index.AppendLine(
                "Call get_from_working_memory(key) for each relevant chunk BEFORE drawing conclusions. " +
                "Do not summarise based on this index alone.");
            index.AppendLine();
            index.AppendLine("| # | Heading | Key |");
            index.AppendLine("|---|---------|-----|");

            for (var i = 0; i < chunks.Count; i++)
            {
                var (heading, content) = chunks[i];
                var key = $"{keyBase}:chunk{i}";
                await workingMemory.SetAsync(sessionId, key, content, ttl: ToolResultChunkTtl, category: "tool-result");
                var label = string.IsNullOrWhiteSpace(heading) ? $"Part {i}" : heading;
                index.AppendLine($"| {i} | {label} | `{key}` |");
            }

            logger.LogInformation(
                "Chunked large tool result for {ToolName}: {Length:N0} chars → {ChunkCount} chunk(s) (threshold {Threshold:N0})",
                toolName, result.Length, chunks.Count, chunkingThreshold);

            return index.ToString().Trim();
        }

        var truncated = result[..chunkingThreshold] +
            $"\n[result truncated — {result.Length - chunkingThreshold:N0} chars omitted]";
        logger.LogWarning(
            "Truncated large tool result for {ToolName}: {Length:N0} chars (no working memory available, threshold {Threshold:N0})",
            toolName, result.Length, chunkingThreshold);
        return truncated;
    }

    private static string SanitizeKeySegment(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 40));
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
            if (sb.Length == 40) break;
        }
        return sb.ToString();
    }
}

/// <summary>
/// Extension methods for wrapping <see cref="AIFunction"/> instances with chunking.
/// </summary>
public static class ChunkingAIFunctionExtensions
{
    /// <summary>
    /// Wraps each <see cref="AIFunction"/> with <see cref="ChunkingAIFunction"/> to chunk
    /// large tool results into working memory.
    /// </summary>
    public static IList<AITool> WithChunking(
        this IEnumerable<AIFunction> tools,
        IWorkingMemory workingMemory,
        string? sessionId,
        ModelBehavior modelBehavior,
        ILogger logger)
    {
        return tools
            .Select(t => (AITool)new ChunkingAIFunction(
                t, workingMemory, sessionId, modelBehavior.ToolResultChunkingThreshold, logger))
            .ToList();
    }
}
