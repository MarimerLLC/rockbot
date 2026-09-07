namespace RockBot.Agent.McpBridge.Attachments;

/// <summary>
/// Per-server manifest declaring which tool arguments and which response shapes participate
/// in the MCP attachment passthrough. Deserialized from the <c>attachments</c> block of an
/// entry in <c>mcp.json</c>. When the manifest is absent on a server, the gateway is a no-op
/// for that server.
/// </summary>
public sealed class AttachmentManifest
{
    /// <summary>
    /// Inline-vs-stash threshold in bytes. Outbound files at/above this size are uploaded
    /// via <c>POST /attachments</c> and replaced with <c>{attachmentId}</c>; below the
    /// threshold they are inlined as <c>{name, base64Content}</c>.
    /// </summary>
    public long ThresholdBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Form-data field name used when uploading attachments via <c>POST /attachments</c>.
    /// Defaults to <c>file</c>; override per server when the receiver expects a different name.
    /// </summary>
    public string UploadFieldName { get; set; } = "file";

    /// <summary>
    /// Endpoint path appended to the server's base URL for attachment GET/POST/DELETE.
    /// Defaults to <c>/attachments</c>.
    /// </summary>
    public string EndpointPath { get; set; } = "/attachments";

    /// <summary>
    /// Outbound configuration — which argument paths should be scanned for path references
    /// that the gateway should rewrite into inline base64 or stashed handles before the
    /// underlying tool sees them.
    /// </summary>
    public AttachmentOutboundConfig? Outbound { get; set; }

    /// <summary>
    /// Inbound configuration — which tools accept the gateway-only <c>mode: "save"</c>
    /// argument that triggers response-side rewriting into <c>{path, name, size, mime}</c>.
    /// </summary>
    public AttachmentInboundConfig? Inbound { get; set; }

    /// <summary>
    /// Binary capture configuration — how the bridge handles binary content a server returns
    /// without being asked to stash it. Unlike the rest of this manifest, capture of typed
    /// (image/audio) content blocks is on by default and needs no manifest at all; this block
    /// exists to turn it off, or to declare the response fields of servers that hand back
    /// base64 inside ordinary JSON.
    /// </summary>
    public AttachmentCaptureConfig? Capture { get; set; }
}

/// <summary>
/// Outbound (request-side) attachment configuration.
/// </summary>
public sealed class AttachmentOutboundConfig
{
    /// <summary>
    /// JSON-Pointer-like paths identifying attachment array locations within tool arguments.
    /// First version supports the shape <c>arrayKey[*]</c> — a top-level array key whose
    /// items are objects with a <c>path</c> field that the gateway will rewrite.
    /// </summary>
    public List<string> ParamPaths { get; set; } = [];
}

/// <summary>
/// Inbound (response-side) attachment configuration.
/// </summary>
public sealed class AttachmentInboundConfig
{
    /// <summary>
    /// Tool names eligible for <c>mode: "save"</c> rewriting. The gateway intercepts
    /// <c>save</c> on these tools, rewrites it to <c>stash</c> or <c>inline</c> for the
    /// underlying call, then transforms the result into <c>{path, name, size, mime}</c>.
    /// </summary>
    public List<string> Tools { get; set; } = [];
}

/// <summary>
/// Binary capture configuration. Capture is the fallback for servers that never heard of
/// RockBot's attachment protocol: rather than letting binary content reach the model as text,
/// the bridge writes it to the shared attachments directory and hands back a path.
/// </summary>
public sealed class AttachmentCaptureConfig
{
    /// <summary>
    /// Whether to capture binary content from this server's responses. Defaults to <c>true</c>,
    /// and applies even to servers with no <c>attachments</c> block at all — a base64 image in
    /// the model's context is never the outcome anyone wanted, so this needs no opt-in. Set
    /// <c>false</c> to send binary content through untouched.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Declarative response-field rules for servers that return file bytes as base64 inside an
    /// ordinary JSON response rather than as a typed content block — Gitea's
    /// <c>get_file_contents</c> shape. Typed image and audio blocks need no rule; MCP already
    /// labels them.
    /// </summary>
    public List<AttachmentCaptureRule> Rules { get; set; } = [];
}

/// <summary>
/// One declarative capture rule: which tools it applies to, and which response fields carry the
/// content, its name, and its type.
/// </summary>
/// <remarks>
/// Fields are read from the top level of the response's JSON object, matching the deliberately
/// simple <c>arrayKey[*]</c> shape supported by <see cref="AttachmentOutboundConfig.ParamPaths"/>.
/// Nested pointers can follow when a real server needs them.
/// </remarks>
public sealed class AttachmentCaptureRule
{
    /// <summary>Tool names this rule applies to. A rule with no tools never matches.</summary>
    public List<string> Tools { get; set; } = [];

    /// <summary>Response field holding the base64 payload. Defaults to <c>content</c>.</summary>
    public string ContentField { get; set; } = "content";

    /// <summary>
    /// Response field holding the file name, used for the saved file and to infer its type.
    /// When unset the gateway looks at <c>name</c> and <c>path</c> before falling back to a
    /// generated name.
    /// </summary>
    public string? NameField { get; set; }

    /// <summary>Response field holding the MIME type, when the server sends one.</summary>
    public string? MimeField { get; set; }

    /// <summary>
    /// Response field naming the payload's encoding (e.g. Gitea's <c>encoding</c>). When set and
    /// its value is not <c>base64</c>, the rule declines rather than decoding garbage.
    /// </summary>
    public string? EncodingField { get; set; }
}
