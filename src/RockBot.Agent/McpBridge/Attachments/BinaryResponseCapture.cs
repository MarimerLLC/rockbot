using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.McpBridge.Attachments;

/// <summary>
/// Catches binary content on its way back from an MCP server and puts it on the shared volume
/// instead of in the model's context.
/// </summary>
/// <remarks>
/// <para>
/// The attachment gateway solves file transport for servers that implement RockBot's
/// convention. Capture is the fallback for the ones that don't — the official Gitea server, for
/// instance, whose <c>get_file_contents</c> hands back a repository PNG as base64 in an ordinary
/// JSON response. Left alone that lands in context as ~167K characters of unusable text, gets
/// chunked into working memory, and still never reaches the model as an image (issue #513).
/// </para>
/// <para>
/// Two rules, deliberately different in what they demand of an operator:
/// </para>
/// <list type="number">
///   <item>
///     Typed <c>image</c> and <c>audio</c> content blocks are captured with no configuration at
///     all — MCP has already labelled them, so no guessing is involved.
///   </item>
///   <item>
///     Base64 inside a JSON response is captured only where a manifest rule names the fields.
///     This follows the same reasoning as the manifest itself: sniffing for "a field that looks
///     like base64" is exactly the fragile heuristic the attachment design rejected.
///   </item>
/// </list>
/// <para>
/// Capture never fails a tool call. Anything unexpected — a bad rule, an unwritable volume, a
/// payload that isn't what the rule claimed — logs and returns the server's original response.
/// A tool that worked before this class existed still works.
/// </para>
/// </remarks>
public sealed class BinaryResponseCapture(IAttachmentStorage storage, ILogger? logger = null)
{
    /// <summary>
    /// Tells the model what to do with a path, without naming a tool: which file-reading tools
    /// exist depends on the deployment, and <c>analyze_file</c> in particular is not registered
    /// unless a model tier can see.
    /// </summary>
    private const string CapturedNote =
        "Binary content was saved to the shared volume instead of being returned inline. " +
        "Use a tool that takes a file path to work with it.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Applies both capture rules to a tool result, returning either a rewritten result or the
    /// original when there was nothing to capture.
    /// </summary>
    public async Task<CallToolResult> CaptureAsync(
        string serverName,
        string toolName,
        CallToolResult result,
        AttachmentCaptureConfig? config,
        CancellationToken ct)
    {
        // A null config means "no attachments block in mcp.json", which is the common case and
        // the one this class exists for — capture is on unless an operator turns it off.
        if (config is { Enabled: false }) return result;
        if (result.Content is not { Count: > 0 }) return result;
        if (result.IsError == true) return result;

        try
        {
            var captured = await CaptureTypedBlocksAsync(serverName, toolName, result, ct);
            return await CaptureDeclaredFieldsAsync(serverName, toolName, captured, config, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "Binary capture failed for {Server}/{Tool}; passing the response through unchanged",
                serverName, toolName);
            return result;
        }
    }

    // ── Rule 1: typed image/audio content blocks ──────────────────────────────

    private async Task<CallToolResult> CaptureTypedBlocksAsync(
        string serverName,
        string toolName,
        CallToolResult result,
        CancellationToken ct)
    {
        if (!result.Content!.Any(IsTypedBinary)) return result;

        var rewritten = new List<ContentBlock>(result.Content.Count);
        var capturedCount = 0;

        foreach (var block in result.Content)
        {
            // Data is base64 text on the wire despite its byte-typed property — see
            // McpBinaryPayload. Writing it raw puts "iVBORw0KGgo…" on disk under a .png name,
            // which every downstream reader then rejects as a corrupt image.
            var payload = block switch
            {
                ImageContentBlock img => (Bytes: McpBinaryPayload.Decode(img.Data), Mime: img.MimeType),
                AudioContentBlock audio => (Bytes: McpBinaryPayload.Decode(audio.Data), Mime: audio.MimeType),
                _ => default
            };

            if (payload.Bytes is null)
            {
                rewritten.Add(block);
                continue;
            }

            var name = GenerateFileName(toolName, payload.Mime);
            var fullPath = await storage.WriteAsync(name, payload.Bytes, ct);
            rewritten.Add(DescriptorBlock(fullPath, payload.Bytes.LongLength, payload.Mime));
            capturedCount++;

            logger?.LogInformation(
                "Binary capture: {Server}/{Tool} {Mime} block ({Bytes} bytes) → {Path}",
                serverName, toolName, payload.Mime, payload.Bytes.LongLength, fullPath);
        }

        return capturedCount == 0
            ? result
            : new CallToolResult { Content = rewritten, StructuredContent = result.StructuredContent };
    }

    private static bool IsTypedBinary(ContentBlock block) =>
        block is ImageContentBlock or AudioContentBlock;

    // ── Rule 2: declared base64 fields in a JSON response ─────────────────────

    private async Task<CallToolResult> CaptureDeclaredFieldsAsync(
        string serverName,
        string toolName,
        CallToolResult result,
        AttachmentCaptureConfig? config,
        CancellationToken ct)
    {
        if (config?.Rules is not { Count: > 0 }) return result;

        var rule = config.Rules.FirstOrDefault(r =>
            r.Tools.Any(t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase)));
        if (rule is null) return result;

        for (var i = 0; i < result.Content!.Count; i++)
        {
            if (result.Content[i] is not TextContentBlock text || string.IsNullOrWhiteSpace(text.Text))
                continue;

            if (JsonNode.Parse(text.Text) is not JsonObject payload)
                continue;

            var captured = await TryCaptureFieldAsync(serverName, toolName, payload, rule, ct);
            if (captured is null) continue;

            var rewritten = new List<ContentBlock>(result.Content);
            rewritten[i] = new TextContentBlock { Text = captured.ToJsonString(JsonOptions) };
            return new CallToolResult { Content = rewritten, StructuredContent = result.StructuredContent };
        }

        return result;
    }

    /// <summary>
    /// Decodes and saves the payload a rule points at, returning the response object with the
    /// content field replaced by the file's location — or null when the rule declines.
    /// </summary>
    /// <remarks>
    /// The rest of the response is preserved because it usually carries things worth keeping: a
    /// commit sha, a URL, the server's own size. Only the field that held the bytes is removed.
    /// </remarks>
    private async Task<JsonObject?> TryCaptureFieldAsync(
        string serverName,
        string toolName,
        JsonObject payload,
        AttachmentCaptureRule rule,
        CancellationToken ct)
    {
        if (ReadString(payload, rule.ContentField) is not { Length: > 0 } base64)
            return null;

        if (rule.EncodingField is { Length: > 0 } encodingField
            && ReadString(payload, encodingField) is { Length: > 0 } encoding
            && !encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            // The server told us this isn't base64. Believe it rather than decoding noise.
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            // Not base64. Before declining, check for the worse case: a server that decoded
            // binary as text and destroyed it on the way out. Nothing can recover those bytes,
            // but the response should not be allowed to flood context on its way to being
            // useless. See ReplaceMangledBinary.
            if (LooksMangled(base64))
                return ReplaceMangledBinary(serverName, toolName, payload, rule, base64.Length);

            logger?.LogDebug(
                "Binary capture: {Server}/{Tool} field '{Field}' is not valid base64; leaving the response alone",
                serverName, toolName, rule.ContentField);
            return null;
        }

        var name = ReadString(payload, rule.NameField)
            ?? ReadString(payload, "name")
            ?? PathLeaf(ReadString(payload, "path"));

        var mime = ReadString(payload, rule.MimeField)
            ?? (name is not null ? AttachmentMime.FromFileName(name) : null);

        // The decisive question: is this actually binary? A repository server returns text and
        // images through the same tool and the same base64 field, and capturing a README to
        // disk would take away content the model could have simply read.
        var isBinary = mime is not null
            ? AttachmentMime.IsBinaryMime(mime)
            : AttachmentMime.LooksBinary(bytes);

        if (!isBinary)
            return null;

        name ??= GenerateFileName(toolName, mime);
        var fullPath = await storage.WriteAsync(name, bytes, ct);

        logger?.LogInformation(
            "Binary capture: {Server}/{Tool} field '{Field}' ({Bytes} bytes, {Mime}) → {Path}",
            serverName, toolName, rule.ContentField, bytes.LongLength, mime ?? "unknown", fullPath);

        var rewritten = payload.DeepClone().AsObject();
        RemoveProperty(rewritten, rule.ContentField);
        rewritten["path"] = fullPath;
        rewritten["name"] = Path.GetFileName(fullPath);
        rewritten["size"] = bytes.LongLength;
        rewritten["mime"] = mime;
        rewritten["note"] = CapturedNote;
        return rewritten;
    }

    /// <summary>
    /// Whether a string is binary that some server decoded as text and wrecked in the process.
    /// </summary>
    /// <remarks>
    /// U+FFFD is the giveaway: it is what a UTF-8 decoder substitutes for a byte sequence it
    /// cannot represent, so a PNG run through <c>Encoding.UTF8.GetString</c> comes out studded
    /// with them. The thresholds keep an ordinary document that happens to carry a couple of
    /// encoding glitches out of scope — at eight or more in a kilobyte the file is not text
    /// anyone can use.
    /// </remarks>
    private static bool LooksMangled(string value)
    {
        const int MinLength = 1024;
        const int MinReplacementChars = 8;

        if (value.Length < MinLength) return false;

        var count = 0;
        foreach (var c in value)
        {
            if (c != '�') continue;
            if (++count >= MinReplacementChars) return true;
        }
        return false;
    }

    /// <summary>
    /// Replaces irrecoverable binary with an explanation of why it is irrecoverable.
    /// </summary>
    /// <remarks>
    /// Measured on a live deployment: one repository MCP server returns a 345 KB PNG through its
    /// file tool as UTF-8-decoded text, arriving as 1.37 million characters that chunk into 22
    /// working-memory entries and 22 embedding calls — for bytes that were already destroyed at
    /// the source, since the decode is lossy and no consumer can undo it. The response is worth
    /// nothing and costs a great deal, and an agent that reads the mojibake tends to retry the
    /// same call. So the field is dropped and the reason is stated, which is the only honest
    /// thing left to say about it. The rest of the response — name, sha, url, size — survives,
    /// and is usually enough to fetch the file another way.
    /// </remarks>
    private JsonObject ReplaceMangledBinary(
        string serverName,
        string toolName,
        JsonObject payload,
        AttachmentCaptureRule rule,
        int mangledLength)
    {
        logger?.LogWarning(
            "Binary capture: {Server}/{Tool} returned binary as text in '{Field}' ({Chars} chars, " +
            "unrecoverable); dropping the field rather than letting it into context",
            serverName, toolName, rule.ContentField, mangledLength);

        var rewritten = payload.DeepClone().AsObject();
        RemoveProperty(rewritten, rule.ContentField);
        rewritten["note"] =
            $"This server returned the file's bytes as text, which corrupted them beyond recovery " +
            $"({mangledLength:N0} characters of unusable content, dropped here rather than loaded " +
            "into context). Calling the same tool again returns the same corrupted text. To work " +
            "with this file, fetch it by a route that preserves bytes — a raw download URL, or a " +
            "copy on the shared volume.";
        return rewritten;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static ContentBlock DescriptorBlock(string fullPath, long size, string? mime) =>
        new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new
            {
                path = fullPath,
                name = Path.GetFileName(fullPath),
                size,
                mime,
                note = CapturedNote
            }, JsonOptions)
        };

    /// <summary>
    /// Names a file that arrived without one. The tool name makes the origin recoverable from a
    /// directory listing, and the random suffix keeps concurrent calls from queueing behind
    /// each other on the storage layer's collision suffixes.
    /// </summary>
    private static string GenerateFileName(string toolName, string? mime)
    {
        var stem = new string(toolName.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        if (stem.Length == 0) stem = "capture";
        return $"{stem}-{Guid.NewGuid().ToString("N")[..8]}{AttachmentMime.ToExtension(mime)}";
    }

    private static string? ReadString(JsonObject payload, string? field)
    {
        if (string.IsNullOrEmpty(field)) return null;
        foreach (var (key, value) in payload)
        {
            if (!string.Equals(key, field, StringComparison.OrdinalIgnoreCase)) continue;
            return value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        }
        return null;
    }

    private static void RemoveProperty(JsonObject payload, string field)
    {
        var match = payload.FirstOrDefault(p =>
            string.Equals(p.Key, field, StringComparison.OrdinalIgnoreCase)).Key;
        if (match is not null) payload.Remove(match);
    }

    private static string? PathLeaf(string? path) =>
        string.IsNullOrEmpty(path) ? null : Path.GetFileName(path.Replace('\\', '/'));
}
