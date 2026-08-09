using System.Collections.Concurrent;
using System.Text.Json;
using RockBot.Host;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Applies an exact-match replacement to a single file on the shared volume,
/// leaving the rest of the file byte-for-byte untouched.
/// </summary>
internal sealed class FileEditToolExecutor(FileSystemOptions options) : IToolExecutor
{
    /// <summary>
    /// One lock per resolved path, so concurrent edits to the same file serialize while
    /// edits to different files do not. Entries are never evicted — one small object per
    /// distinct file edited in the process lifetime, bounded by the volume's contents.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.Ordinal);


    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement))
                return Error(request, "Missing required argument: path");
            if (!args.TryGetValue("old_string", out var oldElement))
                return Error(request, "Missing required argument: old_string");
            if (!args.TryGetValue("new_string", out var newElement))
                return Error(request, "Missing required argument: new_string");

            // Each argument must actually be a string. Coalescing a JSON null to ""
            // would turn a malformed call into a silent deletion of the matched text.
            if (pathElement.ValueKind != JsonValueKind.String)
                return Error(request, "Invalid argument: path must be a string.");
            if (oldElement.ValueKind != JsonValueKind.String)
                return Error(request, "Invalid argument: old_string must be a string.");
            if (newElement.ValueKind != JsonValueKind.String)
            {
                return Error(request,
                    "Invalid argument: new_string must be a string. To delete the matched "
                    + "text, pass an empty string.");
            }

            var relativePath = pathElement.GetString()!;
            var oldString = oldElement.GetString()!;
            var newString = newElement.GetString()!;

            if (!TryReadReplaceAll(args, out var replaceAll))
            {
                return Error(request,
                    "Invalid argument: replace_all must be true or false. Silently ignoring "
                    + "it would refuse your edit as ambiguous with no way to see why.");
            }

            var fullPath = FileWriteToolExecutor.SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Error(request, "Invalid path: must be within the shared volume.");

            if (!File.Exists(fullPath))
                return Error(request, $"File not found: {relativePath}. Use file_write to create it.");

            // Serialize edits to the same file: several subagents can be in flight at
            // once, and without this both would read the pre-edit content and the
            // second write would erase the first, each reporting success.
            var gate = PathLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);

            try
            {
                var read = await FileText.ReadAsync(fullPath, ct);
                if (!read.IsSuccess)
                    return Error(request, $"Cannot edit {relativePath}: {read.Error}");

                var original = read.Content!;
                var result = TextEdit.Apply(original, oldString, newString, replaceAll);

                if (!result.IsSuccess)
                    return Error(request, $"Edit failed on {relativePath}: {result.Error}");

                // The atomic write replaces the directory entry, which a writable directory
                // permits even when the file itself is not writable. Probe first so editing
                // keeps the same permission boundary an in-place write would have had.
                if (!CanWrite(fullPath))
                {
                    return Error(request,
                        $"Permission denied: {relativePath} is not writable. It was created by "
                        + "another user on the shared volume; ask an operator to fix its ownership "
                        + "or mode, or write your change to a new file.");
                }

                var written = await FileText.WriteAtomicIfUnchangedAsync(
                    fullPath, read.Bytes!, result.Content!, read.Encoding!, ct);

                if (!written)
                {
                    return Error(request,
                        $"{relativePath} was modified by something else while this edit was "
                        + "being prepared, so the edit was not applied — writing it would have "
                        + "discarded that change. Read the file again and redo the edit.");
                }

                var plural = result.ReplacementCount == 1 ? "occurrence" : "occurrences";
                return new ToolInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Content = $"Replaced {result.ReplacementCount} {plural} in {relativePath} "
                        + $"({original.Length} → {result.Content!.Length} characters).",
                    IsError = false
                };
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            return Error(request, $"Edit failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the optional <c>replace_all</c> flag, accepting a JSON boolean or its
    /// string spelling.
    /// </summary>
    /// <remarks>
    /// The text-based tool-calling path has no schema to coerce types, so a model may
    /// emit <c>"true"</c> rather than <c>true</c>. Treating that as <c>false</c> would
    /// refuse the edit as ambiguous while the caller can see it did pass the flag, so
    /// the string form is accepted and anything else is an explicit error.
    /// </remarks>
    private static bool TryReadReplaceAll(Dictionary<string, JsonElement> args, out bool replaceAll)
    {
        replaceAll = false;

        if (!args.TryGetValue("replace_all", out var element))
            return true;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                replaceAll = true;
                return true;
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;
            case JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed):
                replaceAll = parsed;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the file itself can be opened for writing, independent of its directory.
    /// </summary>
    private static bool CanWrite(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
