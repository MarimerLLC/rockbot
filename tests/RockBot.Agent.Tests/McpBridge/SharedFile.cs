namespace RockBot.Agent.Tests.McpBridge;

/// <summary>Reads files the running bridge rewrites (its ledgers, <c>mcp.json</c>).</summary>
internal static class SharedFile
{
    /// <summary>
    /// Reads a file the bridge may be replacing at that moment. On Windows, the atomic replace
    /// briefly holds the file, so a poll that lands on it gets a sharing violation; retry it.
    /// A missing file is not retried: the exception propagates as from <see cref="File.ReadAllText(string)"/>.
    /// </summary>
    public static string ReadAllText(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 20 && File.Exists(path))
            {
                Thread.Sleep(10);
            }
        }
    }
}
