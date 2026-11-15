using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RockBot.Host;

namespace McpServer.Introspection.Tools;

/// <summary>
/// MCP-facing view of the agent's LLM configuration snapshot on the profile volume.
/// <para>
/// The agent writes <c>llm-config.json</c> at startup; this reads it. The sidecar never sees the
/// agent's LLM secret, and the snapshot holds no keys, so this can describe the configuration
/// without being able to reveal any credential.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class LlmConfigTools(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private string SnapshotPath =>
        configuration["LlmConfig:Path"] ?? Path.Combine("/data/agent", LlmConfigFiles.SnapshotFile);

    [McpServerTool(Name = "get_llm_config")]
    [Description(
        "Returns the LLM models this agent is configured to use, per tier (Low, Balanced, High): " +
        "the provider, the service that hosts the model (e.g. Anthropic API, OpenRouter, Azure AI " +
        "Foundry), the endpoint host and the model id, with the Balanced fallback chain in priority " +
        "order, plus the embedding model and the decision model (the small classifier behind the " +
        "action gate, tier routing and memory dedupe checks: its service, endpoint host, deployment " +
        "and any uses switched off). Never includes API keys. It reflects the configuration " +
        "loaded at the agent's last startup; model changes need a restart, so this is what is " +
        "running now. Use it for questions like 'what model are you?' or 'which provider do you " +
        "use?'. It shows configuration, not which model answered a given request: a later " +
        "model in a fallback chain is only used when the ones before it fail.")]
    public async Task<string> GetLlmConfigAsync()
    {
        if (!File.Exists(SnapshotPath))
            return JsonSerializer.Serialize(new
            {
                message = "No LLM configuration snapshot found. The agent writes it at startup; " +
                          "an agent version older than this tool does not write it, so restart " +
                          "or upgrade the agent.",
                path = SnapshotPath
            }, WriteOptions);

        try
        {
            var json = await ReadTextAsync(SnapshotPath);
            var snapshot = JsonSerializer.Deserialize<LlmConfigSnapshot>(json, ReadOptions)
                           ?? throw new JsonException("The snapshot file is empty.");

            return JsonSerializer.Serialize(new
            {
                summary = snapshot.Describe(),
                loadedAt = snapshot.WrittenAt,
                snapshot.AgentVersion,
                snapshot.FixedTier,
                snapshot.Note,
                snapshot.Tiers,
                snapshot.Embedding,
                snapshot.DecisionModel
            }, WriteOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return JsonSerializer.Serialize(
                new { error = $"Could not read the LLM configuration snapshot: {ex.Message}", path = SnapshotPath },
                WriteOptions);
        }
    }

    /// <summary>Reads with sharing, so a restarting agent rewriting the file never fails this call.</summary>
    private static async Task<string> ReadTextAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
