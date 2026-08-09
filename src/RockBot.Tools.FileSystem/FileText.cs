using System.Text;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Outcome of a <see cref="FileText.ReadAsync"/> call.
/// </summary>
/// <param name="Content">Decoded text, or <c>null</c> when the file could not be decoded.</param>
/// <param name="Encoding">
/// The encoding the file was decoded with — pass it back to
/// <see cref="FileText.WriteAtomicAsync"/> so the file keeps its original form.
/// <c>null</c> on failure.
/// </param>
/// <param name="Bytes">
/// The file's raw bytes as read. Pass them to
/// <see cref="FileText.WriteAtomicIfUnchangedAsync"/> to detect a concurrent writer.
/// <c>null</c> on failure.
/// </param>
/// <param name="Error">Human-readable failure description; <c>null</c> on success.</param>
internal readonly record struct FileTextReadResult(
    string? Content,
    Encoding? Encoding,
    byte[]? Bytes,
    string? Error)
{
    /// <summary>Whether the file was decoded.</summary>
    public bool IsSuccess => Content is not null;
}

/// <summary>
/// Encoding-preserving, crash-safe text IO for in-place file edits.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> paired with
/// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/> is not a
/// round trip: the read sniffs a byte-order mark while the write always emits UTF-8
/// without one. Editing a single word in a UTF-16 document would silently re-encode
/// the whole file. An edit must change only what the caller asked to change, so the
/// encoding detected on read is carried back into the write.
/// </para>
/// <para>
/// A file with no BOM that is not valid UTF-8 is refused rather than decoded. The
/// permissive decoder maps every undecodable byte to U+FFFD, and persisting that
/// would corrupt an entire document to correct one line of it.
/// </para>
/// <para>
/// Writes land through a sibling temporary file and a rename. The obvious in-place
/// overwrite truncates the original before the replacement content is durable, so a
/// cancelled write — a subagent budget expiring, a pod eviction — would leave the
/// document empty. That is a worse failure than the whole-payload rewrite this tool
/// exists to avoid.
/// </para>
/// </remarks>
internal static class FileText
{
    /// <summary>
    /// Reads <paramref name="path"/> as text, reporting the encoding it was decoded with.
    /// </summary>
    internal static async Task<FileTextReadResult> ReadAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var encoding = DetectEncoding(bytes, out var preambleLength);

        try
        {
            var content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return new FileTextReadResult(content, encoding, bytes, null);
        }
        catch (DecoderFallbackException)
        {
            return new FileTextReadResult(
                null,
                null,
                null,
                "the file is not valid UTF-8 and carries no byte-order mark, so its text "
                + "cannot be recovered without guessing an encoding. Editing it would corrupt "
                + "every non-ASCII byte in the file, not just the edited region.");
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> only if the file still holds
    /// <paramref name="expectedBytes"/>, returning <c>false</c> when it does not.
    /// </summary>
    /// <remarks>
    /// A read-modify-write cycle loses data when someone else writes in the middle of
    /// it: both writers start from the same content and the last one to finish erases
    /// the other's change. In-process callers are serialized by a lock, but a script
    /// pod or MCP server sharing the volume is not, so the content is re-checked
    /// immediately before the rename. This narrows the window rather than closing it —
    /// a shared PVC offers no cross-process locking primitive — but it turns a silent
    /// loss into a reported failure the caller can retry.
    /// </remarks>
    internal static async Task<bool> WriteAtomicIfUnchangedAsync(
        string path,
        byte[] expectedBytes,
        string content,
        Encoding encoding,
        CancellationToken ct)
    {
        var current = await File.ReadAllBytesAsync(path, ct);
        if (!current.AsSpan().SequenceEqual(expectedBytes))
            return false;

        await WriteAtomicAsync(path, content, encoding, ct);
        return true;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> in
    /// <paramref name="encoding"/>, replacing the file atomically.
    /// </summary>
    /// <remarks>
    /// The temporary file inherits the original's Unix mode before the rename, so an
    /// edit does not change the permissions of the file it edits.
    /// </remarks>
    internal static async Task WriteAtomicAsync(
        string path,
        string content,
        Encoding encoding,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(content);
            var bytes = new byte[preamble.Length + body.Length];
            preamble.CopyTo(bytes, 0);
            body.CopyTo(bytes, preamble.Length);

            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            CopyUnixFileMode(path, tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Identifies the encoding from a byte-order mark, defaulting to strict UTF-8.
    /// </summary>
    /// <remarks>
    /// Every returned encoding throws on invalid bytes rather than substituting U+FFFD,
    /// and reproduces the file's original BOM (or absence of one) from
    /// <see cref="Encoding.GetPreamble"/>.
    /// </remarks>
    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        // UTF-32LE before UTF-16LE: both open with FF FE.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            preambleLength = 4;
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            preambleLength = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
        }

        preambleLength = 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }

    private static void CopyUnixFileMode(string source, string destination)
    {
        try
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch
        {
            // Non-Unix platforms and filesystems without mode support — best-effort only.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The temp file is already the failure path; nothing useful to add.
        }
    }
}
