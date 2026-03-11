namespace McpServer.Staging;

/// <summary>
/// Manages blob storage on the local filesystem under a configured base path.
/// </summary>
public sealed class StagingRepository
{
    private readonly string _basePath;

    public StagingRepository(IConfiguration configuration)
    {
        _basePath = Path.GetFullPath(configuration["Staging__BasePath"] ?? "/rockbot/staging");
        Directory.CreateDirectory(_basePath);
    }

    private string Resolve(string relativePath)
    {
        if (relativePath.Contains(".."))
            throw new ArgumentException("Path traversal is not allowed.", nameof(relativePath));
        return Path.GetFullPath(Path.Combine(_basePath, relativePath));
    }

    public async Task StoreStreamAsync(string relativePath, Stream stream)
    {
        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var file = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(file);
    }

    public async Task StoreTextAsync(string relativePath, string content)
    {
        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }

    public async Task<string?> ReadTextAsync(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        if (!File.Exists(fullPath)) return null;
        return await File.ReadAllTextAsync(fullPath);
    }

    public Stream? OpenRead(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        if (!File.Exists(fullPath)) return null;
        return File.OpenRead(fullPath);
    }

    public bool Delete(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        if (!File.Exists(fullPath)) return false;
        File.Delete(fullPath);
        return true;
    }

    public IEnumerable<string> List(string? prefix = null)
    {
        if (!Directory.Exists(_basePath))
            return [];
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        return Directory.EnumerateFiles(_basePath, "*", opts)
            .Select(f => Path.GetRelativePath(_basePath, f).Replace('\\', '/'))
            .Where(f => prefix is null || f.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(f => f);
    }

    public string GetAbsolutePath(string relativePath) => Resolve(relativePath);
}
