using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge;

/// <summary>
/// Options for the MCP Bridge service.
/// </summary>
public sealed class McpBridgeOptions
{
    /// <summary>
    /// Path to the mcp.json configuration file.
    /// </summary>
    public string ConfigPath { get; set; } = "mcp.json";

    /// <summary>
    /// Default timeout in milliseconds for MCP server calls when no per-request timeout is supplied.
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// Maximum tool-call timeout in milliseconds.
    /// Caller-requested and per-server timeouts are capped at this value.
    /// </summary>
    public int MaxTimeoutMs { get; set; } = 900_000;

    /// <summary>
    /// Longest text resource, in characters, that <c>mcp_read_resource</c> returns inline (#617).
    /// Longer text is saved to the shared volume and returned as a path, as binary resources
    /// always are.
    /// </summary>
    public int ResourceInlineTextLimit { get; set; } = 32_000;

    /// <summary>
    /// When true, the bridge calls the LLM to generate a one-sentence summary of each
    /// connected server's capabilities before publishing <see cref="McpServersIndexed"/>.
    /// Falls back to a simple tool-list summary if the LLM is unavailable or the call fails.
    /// </summary>
    public bool GenerateLlmSummaries { get; set; } = true;

    /// <summary>
    /// Number of times to retry a failed <c>ConnectServerAsync</c> attempt before giving up.
    /// Retries use exponential backoff starting at <see cref="ConnectRetryBaseDelayMs"/>.
    /// </summary>
    public int ConnectRetryCount { get; set; } = 2;

    /// <summary>
    /// Base delay in milliseconds for the first retry of a failed connection attempt.
    /// Each subsequent retry doubles this value (2 s → 4 s by default).
    /// </summary>
    public int ConnectRetryBaseDelayMs { get; set; } = 2_000;

    /// <summary>
    /// How often the bridge sweeps for servers that are in config but not connected,
    /// and attempts to reconnect them. Set to zero to disable the sweep.
    /// </summary>
    public int ReconnectSweepIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How often the bridge polls the config file's last-write time and size as a
    /// fallback to the <see cref="System.IO.FileSystemWatcher"/> (which can miss
    /// rename-into-place writes and is unreliable on some network/overlay
    /// filesystems such as Longhorn). Set to zero to disable polling. Default 5 s.
    /// </summary>
    public int ConfigPollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// How often, in seconds, each connected server's tool and prompt lists are re-read to catch
    /// a surface change the server didn't announce with list_changed. Checked on the reconnect
    /// sweep, so it only runs when <see cref="ReconnectSweepIntervalSeconds"/> is positive.
    /// 0 disables. A changed surface is published; an unchanged one publishes nothing.
    /// </summary>
    public int SurfaceRefreshIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Elicitation policy applied to every server that does not declare its own
    /// <c>elicitation</c> block. The default answers form-mode questions from the in-flight
    /// tool call; set <c>Mode</c> to <c>decline</c> or <c>off</c> to tighten it fleet-wide.
    /// </summary>
    public McpElicitationConfig DefaultElicitation { get; set; } = new();

    /// <summary>
    /// Most questions handed back (<c>"mode": "handback"</c>) that one session can have waiting
    /// at once. Past this, a server's new question for the session is declined in-band.
    /// </summary>
    public int MaxPendingQuestionsPerSession { get; set; } = 5;

    /// <summary>Most handed-back questions, and so parked calls, the bridge holds at once.</summary>
    public int MaxPendingQuestions { get; set; } = 50;

    /// <summary>
    /// The durable ledger of handed-back questions, which lets a restarted bridge tell each
    /// session about the calls a restart interrupted. A relative path resolves next to
    /// <see cref="ConfigPath"/>. Default <c>mcp/pending-questions.json</c> beside <c>mcp.json</c>.
    /// </summary>
    public string PendingLedgerPath { get; set; } = "mcp/pending-questions.json";

    /// <summary>
    /// Reads <c>DefaultElicitation:Defaults</c> from the bridge's configuration section.
    /// </summary>
    /// <remarks>
    /// <c>ConfigurationBinder</c> cannot bind <see cref="McpElicitationConfig.Defaults"/>
    /// (<c>Dictionary&lt;string, JsonElement&gt;</c>) and silently yields an empty dictionary, so
    /// defaults set in appsettings, Helm values or environment variables would be ignored.
    /// Configuration values are always strings: each becomes a JSON string (an indexed list
    /// becomes an array of strings, for multi-select fields), and the coordinator reads a string
    /// default for a boolean or number field as the literal it spells.
    /// </remarks>
    public static Dictionary<string, JsonElement> ReadElicitationDefaults(IConfiguration bridgeSection)
    {
        var defaults = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in bridgeSection.GetSection("DefaultElicitation:Defaults").GetChildren())
        {
            if (child.Value is not null)
            {
                defaults[child.Key] = JsonSerializer.SerializeToElement(child.Value);
                continue;
            }

            string[] items = [.. child.GetChildren().Select(c => c.Value).OfType<string>()];
            if (items.Length > 0)
                defaults[child.Key] = JsonSerializer.SerializeToElement(items);
        }

        return defaults;
    }

    /// <summary>
    /// Default MCP servers seeded from infrastructure config (e.g. Helm chart).
    /// On startup, any server listed here that is NOT already in the config file
    /// is added automatically. Existing entries are never overwritten.
    /// Key = server name, Value = SSE URL.
    /// </summary>
    public Dictionary<string, string> DefaultServers { get; set; } = [];
}
