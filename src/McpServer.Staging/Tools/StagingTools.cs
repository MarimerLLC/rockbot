using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServer.Staging.Tools;

[McpServerToolType]
public sealed class StagingTools(StagingRepository repository, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "staging_store")]
    [Description("""
        Write UTF-8 text content directly to a staging file (e.g. 'drafts/report.md').
        Use this when the agent itself generates the content.
        For binary files or large outputs produced by a script, use staging_script_info
        to get the REST API details, then have the script upload via HTTP PUT instead.
        Returns "ok: {path}" on success or an error string.
        """)]
    public async Task<string> StoreAsync(string path, string content)
    {
        try
        {
            await repository.StoreTextAsync(path, content);
            return $"ok: {path}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "staging_read")]
    [Description("Read the UTF-8 text content of a staging file. Returns the content or an error string.")]
    public async Task<string> ReadAsync(string path)
    {
        try
        {
            var content = await repository.ReadTextAsync(path);
            return content ?? $"error: file not found: {path}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "staging_list")]
    [Description("List staging files as a JSON array of relative paths. Optional prefix filters results (e.g. 'drafts/').")]
    public string List(string? prefix = null)
    {
        try
        {
            var files = repository.List(prefix).ToList();
            return JsonSerializer.Serialize(files, _jsonOptions);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "staging_delete")]
    [Description("Delete a staging file. Returns 'ok' or an error string.")]
    public string Delete(string path)
    {
        try
        {
            var deleted = repository.Delete(path);
            return deleted ? "ok" : $"error: file not found: {path}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "staging_get_path")]
    [Description("""
        Returns the absolute local filesystem path for a staging file (e.g. '/rockbot/staging/drafts/report.xlsx').
        Use this path when calling tools that accept a local file path, such as an OneDrive upload tool.
        The file must already exist in staging (uploaded via staging_store or by a script via the REST API).
        """)]
    public string GetPath(string path)
    {
        try
        {
            return repository.GetAbsolutePath(path);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    // Single source of truth for the staging REST API contract (URL, auth, subdirs).
    // ScriptToolSkillProvider deliberately defers to this tool rather than duplicating
    // the contract — update here only, not in both places.
    [McpServerTool(Name = "staging_script_info")]
    [Description("""
        Returns the REST API base URL and HTTP contract for the staging service.
        Call this before generating a script that needs to write files to staging.
        The returned JSON describes how any HTTP client can store and retrieve files:
          - Upload:   PUT  {url}/api/staging/{path}  with the file bytes as the request body
          - Download: GET  {url}/api/staging/{path}
          - Delete:   DELETE {url}/api/staging/{path}
          - List:     GET  {url}/api/staging
        ALL requests (except /health) require the header: X-RockBot-Token: <token>
        Inside every script pod the token is available as the environment variable
        ROCKBOT_STAGING_TOKEN, and the URL as ROCKBOT_STAGING_URL.
        Use subpaths like 'tmp/', 'drafts/', or 'exports/' to organise files by TTL.
        """)]
    public string GetScriptInfo()
    {
        var url = configuration["Staging__ServiceUrl"] ?? "http://rockbot-staging.rockbot.svc.cluster.local";
        var info = new
        {
            url,
            urlEnvVar = "ROCKBOT_STAGING_URL",
            auth = new { header = "X-RockBot-Token", tokenEnvVar = "ROCKBOT_STAGING_TOKEN" },
            subdirs = new { tmp = "1d", drafts = "14d", exports = "14d" }
        };
        return JsonSerializer.Serialize(info, _jsonOptions);
    }
}
