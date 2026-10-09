using System.Text.Json;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Writes a whole file on the shared volume, creating it or replacing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read before overwrite.</b> Replacing an existing file is refused unless
/// <see cref="FileReadLedger"/> shows that the calling session has seen the file's current
/// version in full: by one complete <c>file_read</c>, by paged reads that together covered
/// every line, or because this session wrote that version itself. In issue #664 a model
/// overwrote a deck it had seen only as a capped head and tail. The deck was lost, and
/// nothing on the write path could have noticed. Creating a new file is always allowed.
/// </para>
/// <para>
/// <b>Backup.</b> When an existing file is replaced, its previous content is kept first
/// under <see cref="FileBackup.DirectoryName"/>. See <see cref="FileBackup"/>.
/// </para>
/// </remarks>
internal sealed class FileWriteToolExecutor(FileSystemOptions options, FileReadLedger ledger) : IToolExecutor
{
    public FileWriteToolExecutor(FileSystemOptions options) : this(options, new FileReadLedger()) { }

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement))
                return Error(request, "Missing required argument: path");
            if (!args.TryGetValue("content", out var contentElement))
                return Error(request, "Missing required argument: content");

            var relativePath = pathElement.GetString() ?? string.Empty;
            var content = contentElement.GetString() ?? string.Empty;

            var fullPath = SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Error(request, "Invalid path: must be within the shared volume.");

            var gate = FileLocks.For(fullPath);
            await gate.WaitAsync(ct);
            try
            {
                var backupNote = string.Empty;

                if (File.Exists(fullPath))
                {
                    var previous = await File.ReadAllBytesAsync(fullPath, ct);
                    var coverage = ledger.Check(request.SessionId, fullPath, FileReadLedger.Hash(previous));

                    if (coverage.Status != FileCoverageStatus.Full)
                        return Error(request, RefusalMessage(relativePath, previous, coverage));

                    var backup = await FileBackup.KeepAsync(options.BasePath, fullPath, previous, ct);
                    backupNote = FileBackup.Describe(backup);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, content, ct);

                // Make files world-writable so other containers sharing the volume
                // (script pods, MCP servers) can read and overwrite them.
                FileModes.MakeShared(fullPath);

                // The session wrote this version, so it knows all of it. Hash what is on disk
                // rather than the string, so the identity matches what a later read computes.
                var written = await File.ReadAllBytesAsync(fullPath, ct);
                ledger.RecordFullyKnown(
                    request.SessionId, fullPath, FileReadLedger.Hash(written), LineIndex.CountLines(content));

                return new ToolInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Content = $"Written {content.Length} characters to {relativePath}.{backupNote}",
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
            return Error(request, $"Write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Explains why an overwrite was refused and what to do instead, in terms of the specific
    /// lines the session is missing.
    /// </summary>
    private static string RefusalMessage(string relativePath, byte[] currentBytes, FileCoverage coverage)
    {
        var current = LenientText.Decode(currentBytes);
        var totalLines = LineIndex.CountLines(current);
        var prefix = $"Refusing to overwrite {relativePath}: ";
        const string alternative =
            " file_write replaces the whole file, so anything you have not seen would be lost; file_edit "
            + "changes only the text you name and leaves the rest untouched.";

        switch (coverage.Status)
        {
            case FileCoverageStatus.Partial:
                var next = coverage.FirstUnseenLine;
                return prefix
                    + $"this session has not read the current version in full (you have seen {coverage.DescribeSeen()} "
                    + $"of {totalLines}). Read the rest with file_read offset={next}, or use file_edit for targeted changes."
                    + alternative;

            case FileCoverageStatus.Stale:
                return prefix
                    + "the file has changed since this session last read or wrote it (another session, a subagent, "
                    + $"or a script updated it; it is now {current.Length:N0} chars, {totalLines} lines). Read the "
                    + "current version with file_read before replacing it, or use file_edit for targeted changes."
                    + alternative;

            default:
                return prefix
                    + $"the file already exists ({current.Length:N0} chars, {totalLines} lines) and this session has not "
                    + "read it. Read it with file_read first, or use file_edit for targeted changes. If you meant to "
                    + "create a new file, choose a path that does not exist yet."
                    + alternative;
        }
    }

    internal static string? SafeResolvePath(string basePath, string relativePath)
    {
        var fullBase = Path.GetFullPath(basePath);
        var fullPath = Path.GetFullPath(Path.Combine(fullBase, relativePath.TrimEnd('/', '\\')));
        return fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
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
