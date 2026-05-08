using System.Text.Json;
using System.Text.Json.Serialization;

namespace RockBot.Observation;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for reading and writing
/// <see cref="ObservationState"/>. CamelCase property names match the
/// shape committed to in the design doc.
/// </summary>
internal static class ObservationStateJsonOptions
{
    public static readonly JsonSerializerOptions Instance = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
