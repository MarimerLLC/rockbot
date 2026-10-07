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
    /// with each argument's name, description and required flag. Order of the inputs is ignored.
    /// </summary>
    public static string Server(
        IEnumerable<(string Name, string? Description, string? InputSchema)> tools,
        IEnumerable<McpPromptDefinition> prompts)
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
            sb.Append(prompt.Name).Append(FieldSeparator).Append(prompt.Description).Append(FieldSeparator);
            foreach (var arg in prompt.Arguments)
            {
                sb.Append(arg.Name).Append(FieldSeparator)
                  .Append(arg.Description).Append(FieldSeparator)
                  .Append(arg.Required ? '1' : '0').Append(FieldSeparator);
            }
            sb.Append(RecordSeparator);
        }

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
