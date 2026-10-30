namespace RockBot.Agent.McpBridge;

/// <summary>
/// Auth configuration for an MCP server. Exactly one of <see cref="Profile"/> and
/// <see cref="OAuth"/> is set. Either way refresh tokens never appear in <c>mcp.json</c>.
/// </summary>
public sealed class McpServerAuthConfig
{
    /// <summary>
    /// Named auth profile. Resolved at connect time to an
    /// <c>ITokenProvider</c> registered in DI.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// The MCP authorization spec's OAuth flow (#738): the bridge discovers the server's
    /// authorization server from its 401, and the user signs in from a RockBot user interface.
    /// See <c>design/mcp-oauth.md</c>.
    /// </summary>
    public McpServerOAuthConfig? OAuth { get; set; }

    /// <summary>
    /// Why this block can't be used, or null when it can. A server with an unusable auth block
    /// is never connected: connecting without the auth the operator declared would fail anyway,
    /// and could send requests the operator meant to authenticate without it.
    /// </summary>
    public string? Validate()
    {
        var hasProfile = !string.IsNullOrWhiteSpace(Profile);
        if (hasProfile == (OAuth is not null))
            return "set exactly one of 'auth.profile' and 'auth.oauth'";

        if (OAuth?.ClientMetadataDocumentUri is { Length: > 0 } cimd
            && !Uri.TryCreate(cimd, UriKind.Absolute, out _))
            return $"'auth.oauth.clientMetadataDocumentUri' ('{cimd}') is not an absolute URL";

        return null;
    }
}

/// <summary>
/// OAuth client settings for an MCP server that uses the MCP authorization spec. Everything is
/// optional: with nothing set the bridge registers itself with the authorization server
/// (dynamic client registration), which only some authorization servers allow. Entra ID allows
/// neither DCR nor client metadata documents, so an Entra-protected server needs a pre-registered
/// <see cref="ClientId"/>.
/// </summary>
public sealed class McpServerOAuthConfig
{
    /// <summary>A pre-registered client id. When omitted, the bridge uses a client metadata document or DCR.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The client secret for a confidential client. <c>${ENV_VAR}</c> references are expanded, so
    /// the secret itself can live in a Kubernetes Secret rather than in <c>mcp.json</c>.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Scopes to ask for when neither the server's 401 nor its protected-resource metadata names
    /// any. The server's own scopes take precedence.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// A client id metadata document URL, used as the client id when the authorization server
    /// supports them.
    /// </summary>
    public string? ClientMetadataDocumentUri { get; set; }

    /// <summary>Extra query parameters for the authorization request (e.g. <c>prompt</c>).</summary>
    public Dictionary<string, string> AdditionalAuthorizationParameters { get; set; } = [];
}
