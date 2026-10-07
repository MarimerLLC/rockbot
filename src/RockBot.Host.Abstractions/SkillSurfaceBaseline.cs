namespace RockBot.Host;

/// <summary>
/// The MCP server surface an <c>mcp/{server}</c> skill was written against, recorded when the
/// skill's content is written so a later surface change can mark the skill stale.
/// <para>
/// Only a successful read produces a baseline — <see cref="Fingerprint"/> is never null. When the
/// surface can't be read at write time the skill gets no baseline at all and reads as unknown,
/// never as a baseline that would claim <c>stale</c> forever (mcp-aggregator#41).
/// </para>
/// </summary>
/// <param name="ServerName">Server name the skill was written for (the <c>{server}</c> in <c>mcp/{server}</c>).</param>
/// <param name="ServerId">Stable id of the server entry at the time, when the bridge reported one.</param>
/// <param name="Fingerprint">Server surface fingerprint (tools, descriptions, schemas and prompts).</param>
/// <param name="Version">Server-reported <c>serverInfo.version</c>, when the server reported one.</param>
/// <param name="IdentityHash">Hash of the server entry's canonical identity, used to recognise a renamed server.</param>
/// <param name="RecordedAt">When the baseline was taken.</param>
public sealed record SkillSurfaceBaseline(
    string ServerName,
    string? ServerId,
    string Fingerprint,
    string? Version,
    string? IdentityHash,
    DateTimeOffset RecordedAt);
