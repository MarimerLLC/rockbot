namespace RockBot.Agent.McpBridge.Attachments;

/// <summary>
/// Default <see cref="IAttachmentStorage"/> backed by the shared filesystem volume mounted at
/// <c>${ROCKBOT_SHARED_PATH}/attachments</c>, falling back to <c>/rockbot/shared/attachments</c>
/// when the env var is unset (e.g. local dev outside Docker). The directory is created on
/// construction so callers can write into it without bootstrapping.
/// </summary>
public sealed class AttachmentStorage : IAttachmentStorage
{
    public string BasePath { get; }

    public AttachmentStorage()
        : this(ResolveDefaultBasePath())
    {
    }

    /// <summary>
    /// Constructs an instance pointing at an explicit directory — primarily for tests.
    /// </summary>
    public AttachmentStorage(string basePath)
    {
        BasePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        Directory.CreateDirectory(BasePath);
    }

    private static string ResolveDefaultBasePath()
    {
        var sharedRoot = Environment.GetEnvironmentVariable("ROCKBOT_SHARED_PATH");
        if (string.IsNullOrWhiteSpace(sharedRoot))
            sharedRoot = "/rockbot/shared";
        return Path.Combine(sharedRoot, "attachments");
    }

    public Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        var resolved = ResolveReadPath(path);
        return File.ReadAllBytesAsync(resolved, ct);
    }

    private string ResolveReadPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Attachment path must not be empty.", nameof(path));

        // Bare filenames resolve under the base directory; absolute paths must point inside
        // the base directory so callers can't reach arbitrary filesystem locations through
        // model-controlled inputs.
        if (!Path.IsPathRooted(path))
            return Path.Combine(BasePath, path);

        var fullBase = Path.GetFullPath(BasePath);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Attachment path '{path}' is outside the shared attachments directory '{BasePath}'.");
        return fullPath;
    }

    public async Task<string> WriteAsync(string preferredFileName, byte[] data, CancellationToken ct)
    {
        Directory.CreateDirectory(BasePath);
        var sanitized = SanitizeFileName(preferredFileName);
        var finalName = ResolveCollision(sanitized);
        var fullPath = Path.Combine(BasePath, finalName);
        await File.WriteAllBytesAsync(fullPath, data, ct);
        return fullPath;
    }

    private string ResolveCollision(string name)
    {
        var fullPath = Path.Combine(BasePath, name);
        if (!File.Exists(fullPath))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < int.MaxValue; i++)
        {
            var candidate = $"{stem}-{i}{ext}";
            if (!File.Exists(Path.Combine(BasePath, candidate)))
                return candidate;
        }
        throw new IOException($"Could not resolve filename collision for '{name}'.");
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "attachment";

        // Always keep just the leaf so server responses can't write into arbitrary directories.
        var leaf = Path.GetFileName(name);
        if (string.IsNullOrEmpty(leaf))
            leaf = name;

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = leaf.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (Array.IndexOf(invalid, buffer[i]) >= 0)
                buffer[i] = '_';
        }
        var sanitized = new string(buffer);
        return string.IsNullOrWhiteSpace(sanitized) ? "attachment" : sanitized;
    }
}
