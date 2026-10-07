using System.Text.RegularExpressions;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Rules for MCP server names and identities. Server names flow into skill names
/// (<c>mcp/{server}</c>), file paths (<c>tool-defaults/{server}.json</c>) and, with typed wrapper
/// tools, into LLM-visible tool names (<c>{server}__{tool}</c>) — so a new name must be short,
/// plain, and free of the <c>__</c> separator.
/// </summary>
public static partial class McpServerNames
{
    /// <summary>Separator between server and tool in a typed wrapper tool name.</summary>
    public const string ToolSeparator = "__";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

    /// <summary>
    /// Returns why <paramref name="name"/> can't be used for a new server, or null when it can.
    /// </summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "A server name is required.";
        if (!ValidName().IsMatch(name))
        {
            return $"Server name '{name}' is invalid: use 1-64 characters from letters, digits, '_', '.' and '-', " +
                   "starting with a letter or digit.";
        }
        if (name.Contains(ToolSeparator, StringComparison.Ordinal))
            return $"Server name '{name}' is invalid: it may not contain '{ToolSeparator}', which separates server and tool names.";
        return null;
    }

    /// <summary>
    /// A new stable server id: 12 lowercase hex characters. Assigned once per server entry and
    /// never changed — a rename is an unregister plus a register, and gets a new id.
    /// </summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
