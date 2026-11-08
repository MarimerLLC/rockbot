using System.ComponentModel;
using System.Text.Json;
using McpServer.Graph.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpServer.Graph.Tools;

[McpServerToolType]
public sealed class DriveTools(GraphDriveClient client)
{
    private const int MaxTop = 200;
    private const int MaxSearchLength = 200;
    private const string PathDescription =
        "A drive-scoped path: '/{alias}/{item path}', e.g. '/finance-ro/Budgets/2026.xlsx', or just " +
        "'/{alias}' for the drive root. {alias} is one of the drives configured on this server; it is " +
        "never a Graph id. Which aliases this call is actually allowed to touch is enforced outside " +
        "this server and is not discoverable by asking it — retry with a different alias if one is refused.";

    [McpServerTool(Name = "list_drive_items", ReadOnly = true)]
    [Description("Lists the children of a OneDrive/SharePoint folder (or the drive root), alphabetically. " +
                 "Returns a JSON array of { name, path, isFolder, size, modified }." + ToolArgs.UntrustedNote)]
    public async Task<string> ListDriveItemsAsync(
        [Description(PathDescription)] string path,
        [Description("Number of items to return, 1-200. Defaults to 50.")] int top = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ToolArgs.RequireTop(top, MaxTop);
            var items = await client.ListDriveItemsAsync(path, top, cancellationToken);
            return Serialize(items);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "get_drive_item", ReadOnly = true)]
    [Description("Gets one OneDrive/SharePoint file or folder's metadata. " +
                 "Returns JSON { name, path, isFolder, size, modified, mimeType, webUrl }." + ToolArgs.UntrustedNote)]
    public async Task<string> GetDriveItemAsync(
        [Description(PathDescription)] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var item = await client.GetDriveItemAsync(path, cancellationToken);
            return Serialize(item);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "search_drive_items", ReadOnly = true)]
    [Description("Searches a OneDrive/SharePoint folder (and its subfolders) by name and content. " +
                 "Returns a JSON array of { name, path, isFolder, size, modified }." + ToolArgs.UntrustedNote)]
    public async Task<string> SearchDriveItemsAsync(
        [Description(PathDescription + " Searched folder, e.g. '/finance-ro' to search the whole drive.")] string path,
        [Description("Search text. Max 200 characters.")] string query,
        [Description("Number of items to return, 1-200. Defaults to 50.")] int top = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            query = ToolArgs.RequireText(query, nameof(query), MaxSearchLength);
            ToolArgs.RequireTop(top, MaxTop);
            var items = await client.SearchDriveItemsAsync(path, query, top, cancellationToken);
            return Serialize(items);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "download_drive_file", ReadOnly = true)]
    [Description("Downloads one OneDrive/SharePoint file. Text files (txt, csv, json, html, md, ...) come back as " +
                 "'text'. Other files (PDF, Office documents, images, archives) are saved to the shared volume and " +
                 "come back as { name, size, content }: the agent's bridge replaces content with a path you can pass " +
                 "to analyze_file or file_read. Files over the size limit (25 MB by default) are refused." + ToolArgs.UntrustedNote)]
    public async Task<string> DownloadDriveFileAsync(
        [Description(PathDescription)] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await client.DownloadDriveFileAsync(path, cancellationToken);
            return Serialize(content);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "upload_drive_file", Destructive = false)]
    [Description("Uploads a file to OneDrive/SharePoint, up to the configured file size limit (25 MB by default). " +
                 "Fails if the file already exists unless overwrite is true. Returns the created item's " +
                 "{ name, path, isFolder, size, modified }.")]
    public async Task<string> UploadDriveFileAsync(
        [Description(PathDescription + " Must name the file to create, not an existing folder.")] string path,
        [Description("The file's content, base64-encoded.")] string content_base64,
        [Description("If true, replaces an existing file at this path. Defaults to false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var item = await client.UploadDriveFileAsync(path, content_base64, overwrite, cancellationToken);
            return Serialize(item);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "create_drive_folder", Destructive = false)]
    [Description("Creates a folder in OneDrive/SharePoint. Fails if a folder with that name already exists at that " +
                 "path. Returns the created folder's { name, path, isFolder, size, modified }.")]
    public async Task<string> CreateDriveFolderAsync(
        [Description(PathDescription + " Must name the folder to create.")] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var item = await client.CreateDriveFolderAsync(path, cancellationToken);
            return Serialize(item);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "delete_drive_item", Destructive = true)]
    [Description("Permanently deletes a file or folder (and everything inside it) from OneDrive/SharePoint. " +
                 "There is no undo through this tool.")]
    public async Task<string> DeleteDriveItemAsync(
        [Description(PathDescription)] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await client.DeleteDriveItemAsync(path, cancellationToken);
            return Serialize(new { deleted = path });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
