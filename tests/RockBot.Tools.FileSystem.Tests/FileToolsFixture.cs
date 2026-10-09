using System.Text;
using System.Text.Json;

namespace RockBot.Tools.FileSystem.Tests;

/// <summary>
/// A temp-directory volume with read/write/edit/list executors sharing one ledger, the way
/// the registrar wires them. Never touches /rockbot/shared.
/// </summary>
internal sealed class FileToolsFixture : IDisposable
{
    public FileToolsFixture(int? readMaxChars = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "rockbot-file-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Options = new FileSystemOptions { BasePath = Root };
        if (readMaxChars is { } max)
            Options.FileReadMaxChars = max;
        Ledger = new FileReadLedger();
    }

    public string Root { get; }
    public FileSystemOptions Options { get; }
    public FileReadLedger Ledger { get; }

    public string FullPath(string relative) => Path.Combine(Root, relative);

    public string WriteRaw(string relative, string content)
    {
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string ReadRaw(string relative) => File.ReadAllText(FullPath(relative));

    public Task<ToolInvokeResponse> ReadAsync(string session, string path, int? offset = null, int? limit = null)
    {
        var args = new Dictionary<string, object> { ["path"] = path };
        if (offset is { } o) args["offset"] = o;
        if (limit is { } l) args["limit"] = l;
        return new FileReadToolExecutor(Options, Ledger).ExecuteAsync(Request("file_read", session, args), CancellationToken.None);
    }

    public Task<ToolInvokeResponse> WriteAsync(string session, string path, string content) =>
        new FileWriteToolExecutor(Options, Ledger).ExecuteAsync(
            Request("file_write", session, new { path, content }), CancellationToken.None);

    public Task<ToolInvokeResponse> EditAsync(string session, string path, string oldString, string newString) =>
        new FileEditToolExecutor(Options, Ledger).ExecuteAsync(
            Request("file_edit", session, new { path, old_string = oldString, new_string = newString }),
            CancellationToken.None);

    public Task<ToolInvokeResponse> ListAsync(string? prefix = null) =>
        new FileListToolExecutor(Options).ExecuteAsync(
            Request("file_list", "s", prefix is null ? new { } : new { prefix }), CancellationToken.None);

    /// <summary>Reads every page of <paramref name="path"/> and returns the pages' text, footers stripped.</summary>
    public async Task<(string Text, List<string> Responses)> ReadAllPagesAsync(string session, string path)
    {
        var responses = new List<string>();
        var text = new StringBuilder();
        int? offset = null;

        for (var guard = 0; guard < 1000; guard++)
        {
            var response = await ReadAsync(session, path, offset);
            Assert.IsFalse(response.IsError, response.Content);
            responses.Add(response.Content!);

            var (page, next) = ParsePage(response.Content!);
            text.Append(page);
            if (next is null)
                return (text.ToString(), responses);
            offset = next;
        }

        Assert.Fail("Paging did not terminate.");
        return default;
    }

    /// <summary>Splits a paged response into its file text and the next offset (null at end of file).</summary>
    public static (string Page, int? NextOffset) ParsePage(string response)
    {
        var headerEnd = response.IndexOf('\n');
        Assert.IsTrue(response.StartsWith("[file_read ", StringComparison.Ordinal), "Paged response must start with a header.");
        var footerStart = response.LastIndexOf("\n[lines ", StringComparison.Ordinal);
        Assert.IsTrue(footerStart > 0, "Paged response must end with a footer.");

        var page = response[(headerEnd + 1)..(footerStart + 1)];
        var footer = response[(footerStart + 1)..];

        const string marker = "offset=";
        var at = footer.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            StringAssert.Contains(footer, "end of file");
            return (page, null);
        }

        var digits = new string(footer[(at + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
        return (page, int.Parse(digits));
    }

    private static ToolInvokeRequest Request(string tool, string session, object args) => new()
    {
        ToolCallId = "call_1",
        ToolName = tool,
        SessionId = session,
        Arguments = JsonSerializer.Serialize(args),
    };

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
