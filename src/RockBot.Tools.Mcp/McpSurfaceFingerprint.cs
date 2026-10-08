using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RockBot.Tools.Mcp;

/// <summary>
/// SHA-256 fingerprints of what an MCP server exposes, used to tell whether its surface changed:
/// cached schemas are invalidated, summaries regenerated and (later) skills marked stale only
/// when a fingerprint moves.
/// <para>
/// Descriptions and schemas are both hashed — a server can keep every name and rewrite every
/// description and schema, and a names-only hash would call it unchanged (mcp-aggregator#41).
/// Schemas are canonicalised first (object keys sorted at every level, array order kept), so a
/// server that merely reorders keys doesn't look changed. A null or absent schema hashes
/// differently from <c>{}</c>.
/// </para>
/// </summary>
public static class McpSurfaceFingerprint
{
    private const char FieldSeparator = '\u001F';
    private const char RecordSeparator = '\u001E';
    private const char SectionSeparator = '\u001D';

    /// <summary>Fingerprint of one tool: its name, description and canonical input schema.</summary>
    public static string Tool(string name, string? description, string? inputSchema)
    {
        var sb = new StringBuilder();
        AppendTool(sb, name, description, inputSchema);
        return Hash(sb);
    }

    /// <summary>
    /// Fingerprint of a whole server surface: tools sorted by name, then prompts sorted by name
    /// with each argument's name, description and required flag, then resources and templates
    /// (#617). Order of the inputs is ignored.
    /// <para>
    /// The resource section is appended only when the server lists any, so a server without
    /// resources keeps the fingerprint it had before resources were bridged: a moved fingerprint
    /// marks every <c>mcp/{server}</c> skill stale and regenerates the summary.
    /// </para>
    /// </summary>
    public static string Server(
        IEnumerable<(string Name, string? Description, string? InputSchema)> tools,
        IEnumerable<McpPromptDefinition> prompts,
        IEnumerable<McpResourceDefinition>? resources = null)
    {
        var sb = new StringBuilder();
        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            AppendTool(sb, tool.Name, tool.Description, tool.InputSchema);
            sb.Append(RecordSeparator);
        }

        sb.Append(SectionSeparator);

        foreach (var prompt in prompts.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            AppendPrompt(sb, prompt, prompt.Arguments);
            sb.Append(RecordSeparator);
        }

        var sortedResources = (resources ?? [])
            .OrderBy(r => r.IsTemplate)
            .ThenBy(r => r.Uri, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        if (sortedResources.Count > 0)
        {
            sb.Append(SectionSeparator).Append("resources").Append(SectionSeparator);
            foreach (var resource in sortedResources)
            {
                AppendResource(sb, resource);
                sb.Append(RecordSeparator);
            }
        }

        return Hash(sb);
    }

    /// <summary>
    /// Fingerprint of one prompt: its name, description, then each argument's name, description
    /// and required flag, arguments sorted by name so a server that merely reorders them doesn't
    /// look changed (#616). <see cref="Server"/> keeps the server's order, so that existing
    /// fingerprints don't move.
    /// </summary>
    public static string Prompt(McpPromptDefinition prompt)
    {
        var sb = new StringBuilder();
        AppendPrompt(sb, prompt, prompt.Arguments.OrderBy(a => a.Name, StringComparer.Ordinal));
        return Hash(sb);
    }

    /// <summary>
    /// Canonical JSON text: object keys sorted ordinally at every level, array order kept, no
    /// insignificant whitespace. Null or blank input becomes <c>""</c>; text that isn't JSON is
    /// returned as-is so it still contributes to the hash.
    /// </summary>
    public static string Canonicalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                WriteCanonical(writer, doc.RootElement);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void AppendPrompt(StringBuilder sb, McpPromptDefinition prompt, IEnumerable<McpPromptArgument> arguments)
    {
        sb.Append(prompt.Name).Append(FieldSeparator).Append(prompt.Description).Append(FieldSeparator);
        foreach (var arg in arguments)
        {
            sb.Append(arg.Name).Append(FieldSeparator)
              .Append(arg.Description).Append(FieldSeparator)
              .Append(arg.Required ? '1' : '0').Append(FieldSeparator);
        }
    }

    private static void AppendResource(StringBuilder sb, McpResourceDefinition resource) =>
        sb.Append(resource.IsTemplate ? 'T' : 'R').Append(FieldSeparator)
          .Append(resource.Uri).Append(FieldSeparator)
          .Append(resource.Name).Append(FieldSeparator)
          .Append(resource.Title).Append(FieldSeparator)
          .Append(resource.Description).Append(FieldSeparator)
          .Append(resource.MimeType).Append(FieldSeparator)
          .Append(resource.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void AppendTool(StringBuilder sb, string name, string? description, string? inputSchema) =>
        sb.Append(name).Append(FieldSeparator)
          .Append(description).Append(FieldSeparator)
          .Append(Canonicalize(inputSchema));

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Hash(StringBuilder sb) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
}
