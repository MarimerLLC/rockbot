using System.Text.Json;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Per-server policy for MCP elicitation — the <c>elicitation/create</c> request a server
/// sends back to the client mid-tool-call to ask a question it needs answered before it
/// can finish. Configured under a server's <c>elicitation</c> block in <c>mcp.json</c>, or
/// bridge-wide via <c>McpBridge:DefaultElicitation</c>.
/// </summary>
/// <remarks>
/// <para>
/// Elicitation is the one place where an MCP server drives the conversation, so it gets an
/// explicit policy rather than an implicit "whatever the LLM says". The bridge never hands a
/// server a value it has not validated against the server's own requested schema, and never
/// answers a field that looks like a credential.
/// </para>
/// </remarks>
public sealed class McpElicitationConfig
{
    /// <summary>Answer elicitations from configured defaults, then the responder (LLM).</summary>
    public const string ModeAuto = "auto";

    /// <summary>Advertise the capability but always answer <c>decline</c>.</summary>
    public const string ModeDecline = "decline";

    /// <summary>Do not advertise the elicitation capability at all for this server.</summary>
    public const string ModeOff = "off";

    /// <summary>
    /// Hand the question to the calling agent as the tool result and resume the call when it
    /// answers with <c>mcp_answer</c> (see <c>design/mcp-elicitation-handback.md</c>). Only in a
    /// server's own policy: in <c>McpBridge:DefaultElicitation</c> it is treated as
    /// <see cref="ModeAuto"/>. A question that can't be handed back (an older server, or a caller
    /// that can't answer) gets the server's responder, as in <see cref="ModeAuto"/>.
    /// </summary>
    public const string ModeHandback = "handback";

    /// <summary>
    /// One of <see cref="ModeAuto"/>, <see cref="ModeDecline"/>, <see cref="ModeOff"/> or
    /// <see cref="ModeHandback"/>.
    /// An unrecognized value is treated as <see cref="ModeDecline"/> (fail closed) — a typo
    /// must never silently widen what the bridge is willing to answer.
    /// </summary>
    public string Mode { get; set; } = ModeAuto;

    /// <summary>
    /// Maximum number of elicitation rounds the bridge will answer within a single tool call.
    /// Past this, every further request is declined. Zero disables answering entirely.
    /// </summary>
    /// <remarks>
    /// Without a cap, a server that keeps re-asking because it dislikes the answer would spin
    /// the bridge (and the LLM behind it) until the tool-call timeout fires.
    /// </remarks>
    public int MaxPerCall { get; set; } = 3;

    /// <summary>
    /// Deterministic answers by field name, applied before the responder is consulted.
    /// Values are raw JSON and must still satisfy the schema the server sent, so a stale
    /// default is rejected rather than forwarded.
    /// </summary>
    /// <remarks>
    /// This is the way to auto-answer a server's routine "which mailbox?" / "confirm?" prompt
    /// without involving the LLM at all. Matching is case-insensitive on the field name.
    /// </remarks>
    public Dictionary<string, JsonElement> Defaults { get; set; } = [];

    /// <summary>
    /// Field names this server may never be answered for, in addition to the built-in
    /// credential heuristics in <see cref="McpSensitiveFieldDetector"/>. A request touching
    /// one of these fields is declined whole — partial answers would leak which fields the
    /// bridge is willing to fill.
    /// </summary>
    public List<string> DeniedFields { get; set; } = [];

    /// <summary>
    /// Budget in milliseconds for the responder (the LLM call) to produce an answer. The
    /// elicitation runs inside the caller's tool-call timeout, so this is deliberately a
    /// fraction of it: better a declined elicitation than a timed-out tool call.
    /// </summary>
    public int ResponderTimeoutMs { get; set; } = 20_000;

    /// <summary>
    /// Key of the <see cref="IMcpElicitationResponder"/> that answers for this server, as
    /// registered with the host's keyed services. Null or blank uses the host's default
    /// responder.
    /// </summary>
    /// <remarks>
    /// A key with no registered responder leaves the server with <em>no</em> responder, so only
    /// configured <see cref="Defaults"/> are answered. A typo narrows what the bridge answers; it
    /// never falls back to a responder the operator did not choose.
    /// </remarks>
    public string? Responder { get; set; }

    /// <summary>
    /// How long, in minutes, a handed-back question stays open (<see cref="ModeHandback"/> only).
    /// Past this the parked call is cancelled and a later <c>mcp_answer</c> is told it expired.
    /// Zero or less uses the default of 30.
    /// </summary>
    public int HandbackTtlMinutes { get; set; } = DefaultHandbackTtlMinutes;

    /// <summary>Default for <see cref="HandbackTtlMinutes"/>.</summary>
    public const int DefaultHandbackTtlMinutes = 30;

    /// <summary><see cref="HandbackTtlMinutes"/> as a usable duration.</summary>
    public TimeSpan HandbackTtl =>
        TimeSpan.FromMinutes(HandbackTtlMinutes > 0 ? HandbackTtlMinutes : DefaultHandbackTtlMinutes);

    /// <summary>
    /// Returns the normalized mode, mapping anything unrecognized to <see cref="ModeDecline"/>.
    /// </summary>
    public string ResolveMode()
    {
        if (string.Equals(Mode, ModeAuto, StringComparison.OrdinalIgnoreCase)) return ModeAuto;
        if (string.Equals(Mode, ModeOff, StringComparison.OrdinalIgnoreCase)) return ModeOff;
        if (string.Equals(Mode, ModeHandback, StringComparison.OrdinalIgnoreCase)) return ModeHandback;
        return ModeDecline;
    }

    /// <summary>
    /// Whether the mode string as written is one the bridge recognizes. Used only to log a
    /// warning — an unknown mode still resolves to <see cref="ModeDecline"/>.
    /// </summary>
    public bool IsRecognizedMode =>
        string.Equals(Mode, ModeAuto, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Mode, ModeOff, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Mode, ModeDecline, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Mode, ModeHandback, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The policy as the bridge-wide default may carry it: <see cref="ModeHandback"/> becomes
    /// <see cref="ModeAuto"/>, because handing a server's question to the agent (and the agent's
    /// answer to the server) is something a server's own policy has to grant. Every other setting
    /// is kept. Returns this instance when there is nothing to change.
    /// </summary>
    public McpElicitationConfig WithoutHandback()
    {
        if (ResolveMode() != ModeHandback)
            return this;

        return new McpElicitationConfig
        {
            Mode = ModeAuto,
            MaxPerCall = MaxPerCall,
            Defaults = Defaults is null ? [] : new Dictionary<string, JsonElement>(Defaults),
            DeniedFields = [.. DeniedFields ?? []],
            ResponderTimeoutMs = ResponderTimeoutMs,
            Responder = Responder,
            HandbackTtlMinutes = HandbackTtlMinutes,
        };
    }
}
