using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace McpServer.TodoApp.Tools;

/// <summary>
/// Serializer options for the tool wire format: snake_case everywhere, matching the snake_case argument names.
/// The persisted store keeps its own camelCase options in <c>TodoRepository</c>.
/// </summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(new LowerCaseNamingPolicy()) }
    };

    /// <summary>
    /// Enum values go out as plain lowercase (<c>biannual</c>, not <c>bi_annual</c> or <c>biAnnual</c>), matching the
    /// documented argument values. Members with <c>[JsonStringEnumMemberName]</c> (e.g. <c>same_day</c>) keep that name.
    /// </summary>
    private sealed class LowerCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) => name.ToLowerInvariant();
    }
}
