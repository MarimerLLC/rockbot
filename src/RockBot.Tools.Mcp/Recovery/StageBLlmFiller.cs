using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Stage B fallback: when no deterministic provider claims a missing required field,
/// ask the <see cref="ModelTier.Low"/> tier for a single JSON value to fill it.
/// No tools, no narrative — single-shot. See <c>design/self-repair.md</c> Phase 1, Stage B.
/// </summary>
public class StageBLlmFiller(
    ILlmClient llm,
    ILogger<StageBLlmFiller> logger)
{
    /// <summary>
    /// Attempts to fill <paramref name="fieldName"/> by prompting a Low-tier LLM.
    /// Returns the parsed JSON value (already converted to a CLR type) or null on
    /// any failure — caller then surfaces the original error with a recovery trail.
    /// </summary>
    public virtual async Task<object?> TryFillAsync(
        string serverName,
        string toolName,
        string fieldName,
        IReadOnlyDictionary<string, object?> existingArgs,
        string? originalErrorText,
        CancellationToken ct)
    {
        try
        {
            var existingJson = JsonSerializer.Serialize(existingArgs);

            var prompt =
                $"Tool: {serverName}/{toolName}\n" +
                $"Required field: {fieldName}\n" +
                $"Original call args: {existingJson}\n" +
                (originalErrorText is { Length: > 0 } ? $"Original error: {originalErrorText}\n" : "") +
                $"Return only a JSON value for {fieldName}. " +
                "Output nothing else — no narration, no markdown fences, no explanation. " +
                "Strings must be JSON-quoted.";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, prompt)
            };

            var response = await llm.GetResponseAsync(
                messages, ModelTier.Low, new ChatOptions(), ct);

            var raw = response.Text?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                logger.LogWarning(
                    "Stage B fill returned empty response for {Server}/{Tool} field {Field}",
                    serverName, toolName, fieldName);
                return null;
            }

            // Strip code fences if the model added them despite instructions.
            raw = StripCodeFence(raw);

            using var doc = JsonDocument.Parse(raw);
            return McpToolExecutor.ConvertJsonElement(doc.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Stage B fill failed for {Server}/{Tool} field {Field}",
                serverName, toolName, fieldName);
            return null;
        }
    }

    internal static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0) return text;

        var inner = text[(firstNewline + 1)..];
        var endFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (endFence >= 0) inner = inner[..endFence];
        return inner.Trim();
    }
}
