using System.Text.Json;

namespace RockBot.Tools;

/// <summary>
/// Per-tool fingerprints of the MCP tools a wisp definition calls (#647). Stored on a wisp
/// resource when it's attached to a skill, so a later change to one of those tools can flag the
/// wisp for re-validation instead of letting it fail on its next run.
/// <para>
/// Works on the definition's JSON rather than the wisp model, so code that can't reference
/// <c>RockBot.Wisp</c> (the dream service, skill tools, repair tickets) can use it.
/// </para>
/// </summary>
public static class WispToolFingerprints
{
    /// <summary>Key for one tool in a fingerprint map.</summary>
    public static string Key(string serverName, string toolName) => $"{serverName}/{toolName}";

    /// <summary>
    /// Splits a <see cref="Key"/> back into server and tool. The server name can't contain
    /// <c>/</c>; the tool name may.
    /// </summary>
    public static bool TryParseKey(string key, out string serverName, out string toolName)
    {
        var slash = key.IndexOf('/');
        if (slash <= 0 || slash == key.Length - 1)
        {
            serverName = toolName = string.Empty;
            return false;
        }

        serverName = key[..slash];
        toolName = key[(slash + 1)..];
        return true;
    }

    /// <summary>
    /// The MCP tools <paramref name="definitionJson"/> refers to, as written: each
    /// <c>gateway: mcp</c> step's <c>server</c> (possibly null, for the typed-name shorthand) and
    /// <c>tool</c>, plus each top-level <c>tools</c> entry (with a null server). Returns an empty
    /// list for anything that isn't a wisp definition.
    /// </summary>
    public static IReadOnlyList<(string? Server, string Tool)> References(string? definitionJson)
    {
        var found = new List<(string?, string)>();
        if (string.IsNullOrWhiteSpace(definitionJson))
            return found;

        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return found;

            if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object
                        || !step.TryGetProperty("gateway", out var gateway)
                        || gateway.ValueKind != JsonValueKind.String
                        || !string.Equals(gateway.GetString(), "mcp", StringComparison.OrdinalIgnoreCase)
                        || !step.TryGetProperty("tool", out var tool)
                        || tool.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(tool.GetString()))
                        continue;

                    var server = step.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(s.GetString())
                        ? s.GetString()
                        : null;
                    found.Add((server, tool.GetString()!));
                }
            }

            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tools.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                        found.Add((null, t.GetString()!));
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON, so not a wisp definition: nothing to fingerprint.
        }

        return found;
    }

    /// <summary>
    /// Fingerprints of every MCP tool <paramref name="definitionJson"/> calls that
    /// <paramref name="directory"/> can resolve and fingerprint right now, keyed by
    /// <see cref="Key"/>. Tools it can't resolve are left out — an unknown isn't recorded as a
    /// baseline. Returns <c>null</c> when nothing resolved.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Capture(string? definitionJson, IMcpToolDirectory? directory)
    {
        if (directory is null)
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (server, tool) in References(definitionJson))
        {
            McpToolEntry? entry;
            try
            {
                entry = directory.Resolve(server, tool);
            }
            catch
            {
                continue;
            }

            if (entry?.Fingerprint is { Length: > 0 } fingerprint)
                map[Key(entry.ServerName, entry.ToolName)] = fingerprint;
        }

        return map.Count > 0 ? map : null;
    }
}
