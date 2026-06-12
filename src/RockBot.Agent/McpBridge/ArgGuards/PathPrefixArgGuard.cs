using System.Text.Json;

namespace RockBot.Agent.McpBridge.ArgGuards;

/// <summary>
/// Built-in "path-prefix" guard: rejects a tool call when any configured string argument
/// resolves outside the allowed path prefixes. Reject-only; never rewrites.
///
/// Motivation: an MCP server that writes files resolves paths inside its OWN pod. A path
/// like /tmp succeeds there but is invisible to every other pod, so the tool reports
/// success while the file is unreachable. This guard pins path arguments to the shared
/// volume so cross-pod paths stay meaningful.
///
/// Options:
/// <code>
/// { "args": ["save_directory"], "allowedPrefixes": ["/rockbot/shared"], "requireArgs": true }
/// </code>
/// </summary>
public sealed class PathPrefixArgGuard : IMcpArgGuard
{
    public const string HandlerName = "path-prefix";

    internal static readonly JsonSerializerOptions OptionsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class PathPrefixOptions
    {
        public List<string> Args { get; set; } = [];
        public List<string> AllowedPrefixes { get; set; } = [];
        public bool RequireArgs { get; set; }
    }

    public void ValidateOptions(JsonElement? options)
    {
        var bound = BindOptions(options);
        if (bound.Args.Count == 0)
            throw new InvalidOperationException(
                $"'{HandlerName}' guard requires a non-empty 'args' list naming the path arguments to validate.");
        if (bound.AllowedPrefixes.Count == 0)
            throw new InvalidOperationException(
                $"'{HandlerName}' guard requires a non-empty 'allowedPrefixes' list. " +
                "An empty list is not allow-all; remove the guard instead.");
        foreach (var prefix in bound.AllowedPrefixes)
        {
            if (NormalizePath(prefix) is null)
                throw new InvalidOperationException(
                    $"'{HandlerName}' guard prefix '{prefix}' is not a valid absolute path.");
        }
    }

    public ValueTask<McpArgGuardResult> ApplyAsync(McpArgGuardContext context, CancellationToken ct)
    {
        var options = BindOptions(context.Options);
        var prefixes = options.AllowedPrefixes
            .Select(NormalizePath)
            .Where(p => p is not null)
            .Cast<string>()
            .ToList();
        var prefixList = $"[{string.Join(", ", prefixes)}]";

        foreach (var argName in options.Args)
        {
            // Case-insensitive lookup, matching the bridge's tolerance for LLM-cased keys.
            var key = context.Arguments.Keys.FirstOrDefault(
                k => string.Equals(k, argName, StringComparison.OrdinalIgnoreCase));

            if (key is null)
            {
                if (options.RequireArgs)
                {
                    return Reject(
                        $"Argument '{argName}' is required for {context.ToolName} on {context.ServerName}: " +
                        $"without it the server saves to a pod-local default path that is invisible to the " +
                        $"agent and script pods. Retry with '{argName}' set to a path under {prefixList}.");
                }
                continue;
            }

            var value = context.Arguments[key];
            if (value is not string path || string.IsNullOrWhiteSpace(path))
            {
                return Reject(
                    $"Argument '{argName}' value '{value}' is not a valid path string. " +
                    $"Retry with '{argName}' set to a path under {prefixList}.");
            }

            var normalized = NormalizePath(path);
            if (normalized is null)
            {
                return Reject(
                    $"Argument '{argName}' value '{path}' is not allowed: relative paths resolve " +
                    $"inside the MCP server's own pod, invisible to the agent and script pods. " +
                    $"Retry with an absolute path under {prefixList}.");
            }

            if (!prefixes.Any(prefix => IsUnderPrefix(normalized, prefix)))
            {
                return Reject(
                    $"Argument '{argName}' value '{path}' is not allowed: paths outside {prefixList} " +
                    $"are pod-local to the MCP server and invisible to the agent and script pods — " +
                    $"the file would be unreachable even though the tool reports success. " +
                    $"Retry with a path under {prefixList}.");
            }
        }

        return ValueTask.FromResult(McpArgGuardResult.Allowed);

        static ValueTask<McpArgGuardResult> Reject(string message) =>
            ValueTask.FromResult(McpArgGuardResult.Reject(message));
    }

    private static PathPrefixOptions BindOptions(JsonElement? options)
    {
        if (options is null || options.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidOperationException(
                $"'{HandlerName}' guard requires an 'options' object with 'args' and 'allowedPrefixes'.");
        return options.Value.Deserialize<PathPrefixOptions>(OptionsJson)
            ?? throw new InvalidOperationException($"'{HandlerName}' guard options could not be parsed.");
    }

    /// <summary>
    /// Lexically normalizes a path: backslashes to '/', resolves '.' and '..' segments,
    /// collapses duplicate separators, strips the trailing slash. Returns null when the
    /// path is relative or traversal climbs above root. Deliberately NOT Path.GetFullPath —
    /// the target filesystem is the Linux MCP-server pod, not the machine running the bridge.
    /// </summary>
    internal static string? NormalizePath(string value)
    {
        var slashed = value.Trim().Replace('\\', '/');
        if (!slashed.StartsWith('/'))
            return null;

        var segments = new List<string>();
        foreach (var segment in slashed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                    if (segments.Count == 0)
                        return null; // traversal above root
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// Boundary-aware prefix check on normalized paths: equal, or under prefix + "/".
    /// Ordinal comparison — Linux paths are case-sensitive (this intentionally differs
    /// from FileWriteToolExecutor.SafeResolvePath, which targets the local filesystem).
    /// "/rockbot/shared" must never match "/rockbot/shared-evil".
    /// </summary>
    internal static bool IsUnderPrefix(string normalizedPath, string normalizedPrefix)
    {
        if (normalizedPrefix == "/")
            return true;
        return string.Equals(normalizedPath, normalizedPrefix, StringComparison.Ordinal)
            || normalizedPath.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal);
    }
}
