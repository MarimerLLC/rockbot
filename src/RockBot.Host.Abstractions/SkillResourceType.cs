using System.Text.Json.Serialization;

namespace RockBot.Host;

/// <summary>
/// The type of a skill sub-resource file, providing guidance to the LLM and tooling
/// about how the file should be interpreted.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SkillResourceType>))]
public enum SkillResourceType
{
    /// <summary>A Python script.</summary>
    Python,

    /// <summary>A wisp automation definition.</summary>
    Wisp,

    /// <summary>A JSON Schema document.</summary>
    JsonSchema,

    /// <summary>A Markdown document.</summary>
    Markdown,

    /// <summary>A plain-text file.</summary>
    Text,

    /// <summary>Any other file type not covered by the above.</summary>
    Other
}
