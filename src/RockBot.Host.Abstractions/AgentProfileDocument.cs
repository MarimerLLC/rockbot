namespace RockBot.Host;

/// <summary>
/// A parsed agent profile document (soul, directives, or style).
/// </summary>
/// <param name="DocumentType">The kind of document (e.g. "soul", "directives", "style").</param>
/// <param name="Preamble">
/// Content before the first <c>##</c> heading, including any <c>#</c> title.
/// Null when the document starts immediately with a <c>##</c> section.
/// </param>
/// <param name="Sections">Sections delimited by <c>##</c> headings.</param>
/// <param name="RawContent">The original, unmodified markdown content.</param>
public sealed record AgentProfileDocument(
    string DocumentType,
    string? Preamble,
    IReadOnlyList<AgentProfileSection> Sections,
    string RawContent);
