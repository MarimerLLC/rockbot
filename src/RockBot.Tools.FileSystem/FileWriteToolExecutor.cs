using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// <b>Identical content counts as read (issue #677).</b> The overwrite is also allowed when
/// the session has fully seen some other path whose recorded content is byte-for-byte the
/// target's current content. The session has then seen everything the write would replace,
/// whatever path it read it under. The refusal text also tells the model not to work
/// around the guard with a renamed copy, and a renamed copy created soon after a refusal
/// is logged (see <see cref="SiblingCopy"/>).
/// </para>
/// <para>
/// <b>Backup.</b> When an existing file is replaced, its previous content is kept first
/// under <see cref="FileBackup.DirectoryName"/>. See <see cref="FileBackup"/>.
/// </para>
/// <para>
/// <b>Permissions.</b> A write the filesystem refuses inside
/// <see cref="FileSystemOptions.AttachmentsDirectory"/> gets an error that explains the area
/// and names the matching <c>drafts/</c> file. See <see cref="AttachmentsArea"/>.
/// </para>
/// </remarks>
internal sealed class FileWriteToolExecutor(FileSystemOptions options, FileReadLedger ledger, ILogger? logger = null)
    : IToolExecutor
{
    private const string NoCopyGuidance =
        " Do not write a renamed copy instead — the original would go stale and later turns would edit the "
        + "wrong file.";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public FileWriteToolExecutor(FileSystemOptions options) : this(options, new FileReadLedger()) { }

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string? relativePath = null;
        string? fullPath = null;

        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement))
                return Error(request, "Missing required argument: path");
            if (!args.TryGetValue("content", out var contentElement))
                return Error(request, "Missing required argument: content");

            relativePath = pathElement.GetString() ?? string.Empty;
            var content = contentElement.GetString() ?? string.Empty;

            fullPath = SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Error(request, "Invalid path: must be within the shared volume.");

            var gate = FileLocks.For(fullPath);
            await gate.WaitAsync(ct);
            try
            {
                var backupNote = string.Empty;
                var created = !File.Exists(fullPath);

                if (!created)
                {
                    var previous = await File.ReadAllBytesAsync(fullPath, ct);
                    var previousHash = FileReadLedger.Hash(previous);
                    var coverage = ledger.Check(request.SessionId, fullPath, previousHash);

                    if (coverage.Status != FileCoverageStatus.Full)
                    {
                        var copy = ledger.FindFullyKnownCopy(request.SessionId, previousHash, excludePath: fullPath);
                        if (copy is null)
                        {
                            ledger.RecordRefusal(request.SessionId, fullPath);
                            return Error(request, RefusalMessage(relativePath, previous, coverage));
                        }

                        _logger.LogInformation(
                            "Overwrite of {Path} allowed for session {SessionId}: it has fully read {Source}, "
                            + "whose content is identical to the file's current content",
                            relativePath, request.SessionId, Relative(copy));
                    }

                    // Refuse before keeping a backup, so a write that cannot happen leaves nothing behind.
                    if (!WriteAccess.CanWrite(fullPath))
                        return Error(request, PermissionDenied(relativePath, fullPath, systemError: null));

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

                if (created)
                    LogPossibleSiblingCopy(request.SessionId, relativePath, fullPath);

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
        catch (Exception ex) when (fullPath is not null && WriteAccess.IsAccessDenied(ex)
                                   && AttachmentsArea.Contains(options, fullPath))
        {
            return Error(request, AttachmentsArea.Describe(options, relativePath!, ex.Message));
        }
        catch (Exception ex)
        {
            return Error(request, $"Write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs when this new file looks like a renamed copy of a file this session was recently
    /// refused an overwrite of. Changes nothing; it measures whether the refusal text works.
    /// </summary>
    private void LogPossibleSiblingCopy(string? sessionId, string relativePath, string fullPath)
    {
        foreach (var refused in ledger.RecentRefusals(sessionId))
        {
            if (SiblingCopy.IsLikely(fullPath, refused) && File.Exists(refused))
            {
                _logger.LogInformation(
                    "Possible sibling copy after overwrite refusal: {NewPath} (refused: {RefusedPath})",
                    relativePath, Relative(refused));
                return;
            }
        }
    }

    private string PermissionDenied(string relativePath, string fullPath, string? systemError) =>
        AttachmentsArea.Contains(options, fullPath)
            ? AttachmentsArea.Describe(options, relativePath, systemError)
            : $"Write failed: {relativePath} is not writable. It was created by another user on the shared volume; "
              + "ask an operator to fix its ownership or mode.";

    private string Relative(string fullPath) =>
        Path.GetRelativePath(Path.GetFullPath(options.BasePath), fullPath).Replace('\\', '/');

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
                    + NoCopyGuidance
                    + alternative;

            case FileCoverageStatus.Stale:
                return prefix
                    + "the file has changed since this session last read or wrote it (another session, a subagent, "
                    + $"or a script updated it; it is now {current.Length:N0} chars, {totalLines} lines). Read the "
                    + "current version first (one file_read call) or use file_edit for targeted changes."
                    + NoCopyGuidance
                    + alternative;

            default:
                return prefix
                    + $"the file already exists ({current.Length:N0} chars, {totalLines} lines) and this session has not "
                    + "read it. Read it first (one file_read call) or use file_edit for targeted changes."
                    + NoCopyGuidance
                    + " Choose a different path only when you are creating an unrelated new file."
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
