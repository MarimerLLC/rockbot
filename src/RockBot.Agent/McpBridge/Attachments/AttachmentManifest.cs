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
