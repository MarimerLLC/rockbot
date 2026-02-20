namespace RockBot.Host;

/// <summary>
/// A single named section within an agent profile document,
/// delimited by a <c>##</c> heading in the source markdown.
/// </summary>
/// <param name="Name">The section heading text (without the <c>##</c> prefix).</param>
/// <param name="Content">The section body content.</param>
public sealed record AgentProfileSection(string Name, string Content);
