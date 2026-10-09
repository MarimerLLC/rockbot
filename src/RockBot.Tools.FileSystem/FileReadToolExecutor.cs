using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Reads a text file from the shared volume, a whole-line page at a time when it is large.
/// </summary>
/// <remarks>
/// <para>
/// A file that fits <see cref="FileSystemOptions.FileReadMaxChars"/> is returned exactly as
/// stored, with nothing added. Otherwise, or whenever <c>offset</c> or <c>limit</c> is
/// passed, the result is a page with a header and a footer that give the line range, the
/// total, and the offset to continue from. A page never ends partway through a line, so the
/// model never mistakes a cut-off line for the file's real text.
/// </para>
/// <para>
/// Every read is recorded in <see cref="FileReadLedger"/> against the calling session. That
/// record is what lets <c>file_write</c> refuse to overwrite a file this session has only
/// partly seen (issue #664).
/// </para>
/// </remarks>
internal sealed class FileReadToolExecutor(FileSystemOptions options, FileReadLedger ledger) : IToolExecutor
{
    public FileReadToolExecutor(FileSystemOptions options) : this(options, new FileReadLedger()) { }

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement))
                return Error(request, "Missing required argument: path");

            var relativePath = pathElement.GetString() ?? string.Empty;

            if (!TryReadPositiveInt(args, "offset", out var offset, out var offsetError))
                return Error(request, offsetError!);
            if (!TryReadPositiveInt(args, "limit", out var limit, out var limitError))
                return Error(request, limitError!);

            var fullPath = FileWriteToolExecutor.SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Error(request, "Invalid path: must be within the shared volume.");

            if (!File.Exists(fullPath))
                return Error(request, MissingFile.Describe(options.BasePath, relativePath, forEdit: false));

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            var content = LenientText.Decode(bytes);
            var hash = FileReadLedger.Hash(bytes);
            var budget = Math.Max(1, options.FileReadMaxChars);
            var paged = offset is not null || limit is not null;

            if (!paged && content.Length <= budget)
            {
                ledger.RecordFullyKnown(request.SessionId, fullPath, hash, LineIndex.CountLines(content));
                return Ok(request, content);
            }

            var lines = new LineIndex(content);
            var start = offset ?? 1;

            if (lines.Count == 0)
            {
                ledger.RecordFullyKnown(request.SessionId, fullPath, hash, 0);
                return Ok(request, $"[file_read {relativePath}: the file is empty (0 lines)]");
            }

            if (start > lines.Count)
            {
                return Error(request,
                    $"offset {start} is past the end of {relativePath}, which has {lines.Count} lines. "
                    + $"Use an offset between 1 and {lines.Count}.");
            }

            var maxLast = limit is { } l ? Math.Min(lines.Count, start + l - 1) : lines.Count;

            // The first line alone does not fit. Show what fits, say so, and leave the line
            // unrecorded: the session has not seen all of it.
            if (lines.LengthOf(start) > budget)
                return Ok(request, OverlongLine(relativePath, lines, start, budget, content.Length));

            var last = start;
            var used = lines.LengthOf(start);
            while (last < maxLast && used + lines.LengthOf(last + 1) <= budget)
            {
                last++;
                used += lines.LengthOf(last);
            }

            ledger.RecordRead(request.SessionId, fullPath, hash, lines.Count, start, last);

            var page = lines.Slice(start, last);
            var sb = new StringBuilder(page.Length + 300);
            sb.Append(CultureInfo.InvariantCulture,
                $"[file_read {relativePath}: lines {start}–{last} of {lines.Count}]\n");
            sb.Append(page);
            if (!page.EndsWith('\n'))
                sb.Append('\n');
            sb.Append(Footer(start, last, lines.Count, page.Length, content.Length, limitReached: last == maxLast));

            return Ok(request, sb.ToString());
        }
        catch (Exception ex)
        {
            return Error(request, $"Read failed: {ex.Message}");
        }
    }

    private static string Footer(int start, int last, int total, int pageChars, int totalChars, bool limitReached)
    {
        var range = string.Create(CultureInfo.InvariantCulture,
            $"[lines {start}–{last} of {total} ({pageChars:N0} of {totalChars:N0} chars)");

        if (last >= total)
            return range + " — end of file]";

        var reason = limitReached ? string.Empty : " The page budget was reached.";
        return range + string.Create(CultureInfo.InvariantCulture,
            $" — call file_read with offset={last + 1} to continue.{reason}]");
    }

    private static string OverlongLine(string relativePath, LineIndex lines, int line, int budget, int totalChars)
    {
        var text = lines.Slice(line, line);
        var next = line < lines.Count
            ? string.Create(CultureInfo.InvariantCulture, $" Continue with file_read offset={line + 1}.")
            : " This is the last line.";

        var inv = CultureInfo.InvariantCulture;
        return $"[file_read {relativePath}: line {line} of {lines.Count}, partial]\n"
            + text[..budget]
            + $"\n[line {line} is {text.Length.ToString("N0", inv)} chars, longer than the "
            + $"{budget.ToString("N0", inv)}-char page; only its first {budget.ToString("N0", inv)} chars are shown "
            + $"(the file is {totalChars.ToString("N0", inv)} chars). file_read cannot show the rest of this line; "
            + "use a script to process it."
            + next + "]";
    }

    /// <summary>
    /// Reads an optional positive integer. The text-based tool-calling path has no schema to
    /// coerce types, so a numeric string is accepted too.
    /// </summary>
    private static bool TryReadPositiveInt(
        Dictionary<string, JsonElement> args, string name, out int? value, out string? error)
    {
        value = null;
        error = null;

        if (!args.TryGetValue(name, out var element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        var parsed = 0;
        var ok = false;
        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var number)
            && number == Math.Floor(number)
            && number is >= 1 and <= int.MaxValue)
        {
            parsed = (int)number;
            ok = true;
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            ok = int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        }

        if (!ok || parsed < 1)
        {
            error = $"Invalid argument: {name} must be a whole number of 1 or more"
                + (name == "offset" ? " (line numbers start at 1)." : ".");
            return false;
        }

        value = parsed;
        return true;
    }

    private static ToolInvokeResponse Ok(ToolInvokeRequest request, string content) =>
        new()
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = false
        };

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
