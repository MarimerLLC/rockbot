using System.Text.Json;
using RockBot.Host;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Applies an exact-match replacement to a single file on the shared volume,
/// leaving the rest of the file byte-for-byte untouched.
/// </summary>
/// <remarks>
/// <para>
/// A failed match returns the region of the file closest to <c>old_string</c>, with line
/// numbers, so the model can copy the real text instead of giving up and rewriting the whole
/// file (issue #664). See <see cref="NearestRegion"/>.
/// </para>
/// <para>
/// A successful edit keeps the previous content under <see cref="FileBackup.DirectoryName"/>.
/// It also updates <see cref="FileReadLedger"/>. If the session had seen the pre-edit version
/// in full, it now knows the edited version in full. Otherwise its earlier partial coverage is
/// dropped: an edit proves the session knew <c>old_string</c>, not the rest of the file, and the
/// edit may have shifted line numbers.
/// </para>
/// </remarks>
internal sealed class FileEditToolExecutor(FileSystemOptions options, FileReadLedger ledger) : IToolExecutor
{
    public FileEditToolExecutor(FileSystemOptions options) : this(options, new FileReadLedger()) { }

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
                return Error(request, MissingFile.Describe(options.BasePath, relativePath, forEdit: true));

            // Serialize edits to the same file: several subagents can be in flight at
            // once, and without this both would read the pre-edit content and the
            // second write would erase the first, each reporting success. file_write
            // takes the same lock.
            var gate = FileLocks.For(fullPath);
            await gate.WaitAsync(ct);

            try
            {
                var read = await FileText.ReadAsync(fullPath, ct);
                if (!read.IsSuccess)
                    return Error(request, $"Cannot edit {relativePath}: {read.Error}");

                var original = read.Content!;
                var result = TextEdit.Apply(original, oldString, newString, replaceAll);

                if (!result.IsSuccess)
                {
                    var hint = result.Status == TextEditStatus.NotFound
                        ? NearestRegion.Describe(original, oldString)
                        : string.Empty;
                    return Error(request, $"Edit failed on {relativePath}: {result.Error}{hint}");
                }

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

                // Whether the session knew the whole pre-edit version decides what it knows
                // of the post-edit one. Checked before writing, while the hash still matches.
                var knewAll = ledger.Check(request.SessionId, fullPath, FileReadLedger.Hash(read.Bytes!)).Status
                    == FileCoverageStatus.Full;

                // Keep the version being replaced before replacing it, so a bad edit can be undone.
                var backup = await FileBackup.KeepAsync(options.BasePath, fullPath, read.Bytes!, ct);

                var written = await FileText.WriteAtomicIfUnchangedAsync(
                    fullPath, read.Bytes!, result.Content!, read.Encoding!, ct);

                if (!written)
                {
                    return Error(request,
                        $"{relativePath} was modified by something else while this edit was "
                        + "being prepared, so the edit was not applied — writing it would have "
                        + "discarded that change. Read the file again and redo the edit.");
                }

                if (knewAll)
                {
                    var newBytes = await File.ReadAllBytesAsync(fullPath, ct);
                    ledger.RecordFullyKnown(
                        request.SessionId, fullPath, FileReadLedger.Hash(newBytes), LineIndex.CountLines(result.Content!));
                }
                else
                {
                    ledger.Forget(request.SessionId, fullPath);
                }

                var plural = result.ReplacementCount == 1 ? "occurrence" : "occurrences";
                return new ToolInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Content = $"Replaced {result.ReplacementCount} {plural} in {relativePath} "
                        + $"({original.Length} → {result.Content!.Length} characters)."
                        + FileBackup.Describe(backup),
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
