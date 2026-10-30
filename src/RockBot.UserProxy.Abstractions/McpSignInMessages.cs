namespace RockBot.UserProxy;

// MCP OAuth sign-in (#738). An MCP server that protects itself with the MCP authorization spec
// answers 401 until the user signs in. The agent's bridge runs the OAuth client — discovery,
// PKCE, the code exchange, token storage and refresh — and a user interface only runs the
// browser leg: it opens the authorization URL the bridge hands it and relays the redirect's
// code back. The code is useless without the PKCE verifier, which never leaves the bridge, so
// no user interface ever holds a credential. See design/mcp-oauth.md.

/// <summary>
/// Asks the agent to start signing the user in to an MCP server. Published to
/// <see cref="UserProxyTopics.McpSignIn"/> with a reply-to; answered by
/// <see cref="McpSignInStartResponse"/>, and the outcome arrives later as an
/// <see cref="McpSignInStatus"/>.
/// </summary>
public sealed record McpSignInStartRequest
{
    /// <summary>The MCP server's name in the agent's <c>mcp.json</c>.</summary>
    public required string ServerName { get; init; }

    /// <summary>
    /// Where the authorization server should send the browser back — an endpoint of the calling
    /// user interface (Blazor's <c>/mcp-oauth/callback</c>, the CLI's loopback listener). It has
    /// to be one the authorization server accepts for the server's client.
    /// </summary>
    public required string RedirectUri { get; init; }
}

/// <summary>The agent's answer to an <see cref="McpSignInStartRequest"/>.</summary>
public sealed record McpSignInStartResponse
{
    public required string ServerName { get; init; }

    /// <summary>Identifies this sign-in in the <see cref="McpSignInStatus"/> that settles it. Null when it could not start.</summary>
    public string? SignInId { get; init; }

    /// <summary>The page to open in the user's browser. Null when the sign-in could not start.</summary>
    public string? AuthorizationUri { get; init; }

    /// <summary>When the bridge stops waiting for the user to finish.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Why the sign-in could not start, when it could not.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// What the authorization server sent back to a user interface's redirect URI, relayed to the
/// agent on <see cref="UserProxyTopics.McpSignIn"/>. Fire-and-forget: the outcome arrives as an
/// <see cref="McpSignInStatus"/>. The bridge finds the sign-in by <see cref="State"/>, so the
/// relaying interface needn't be the one that started it.
/// </summary>
public sealed record McpSignInCallback
{
    /// <summary>The <c>state</c> query parameter, which ties the redirect to its sign-in.</summary>
    public required string State { get; init; }

    /// <summary>The <c>code</c> query parameter, absent when the user or server refused.</summary>
    public string? Code { get; init; }

    /// <summary>The <c>iss</c> query parameter (RFC 9207), when the authorization server sends one.</summary>
    public string? Iss { get; init; }

    /// <summary>The <c>error</c> query parameter, when the authorization server refused (e.g. <c>access_denied</c>).</summary>
    public string? Error { get; init; }

    /// <summary>The <c>error_description</c> query parameter.</summary>
    public string? ErrorDescription { get; init; }
}

/// <summary>Asks the agent which MCP servers use OAuth sign-in and where each stands.</summary>
public sealed record McpSignInListRequest;

/// <summary>The agent's answer to an <see cref="McpSignInListRequest"/>.</summary>
public sealed record McpSignInListResponse
{
    public List<McpSignInServer> Servers { get; init; } = [];
}

/// <summary>One OAuth-protected MCP server in an <see cref="McpSignInListResponse"/>.</summary>
public sealed record McpSignInServer
{
    public required string ServerName { get; init; }

    /// <summary>One of <see cref="McpSignInState"/>: <c>required</c>, <c>pending</c> or <c>signed_in</c>.</summary>
    public required string State { get; init; }

    /// <summary>Whether the bridge is connected to the server right now.</summary>
    public bool Connected { get; init; }
}

/// <summary>
/// A change in an MCP server's sign-in, broadcast on <see cref="UserProxyTopics.UserResponse"/>
/// as its own message type: a server now needs the user to sign in, or a sign-in finished,
/// failed or expired. Frontends show or clear their sign-in prompt for the server.
/// </summary>
public sealed record McpSignInStatus
{
    public required string ServerName { get; init; }

    /// <summary>One of <see cref="McpSignInState"/>.</summary>
    public required string State { get; init; }

    /// <summary>The sign-in this settles, for the states that settle one.</summary>
    public string? SignInId { get; init; }

    /// <summary>A human-readable explanation, for failures and for <c>required</c>.</summary>
    public string? Message { get; init; }
}

/// <summary>States an <see cref="McpSignInStatus"/> or <see cref="McpSignInServer"/> reports.</summary>
public static class McpSignInState
{
    /// <summary>The server rejects the agent until the user signs in.</summary>
    public const string Required = "required";

    /// <summary>A sign-in has started and waits for the user to finish it in the browser.</summary>
    public const string Pending = "pending";

    /// <summary>The bridge holds tokens for the server.</summary>
    public const string SignedIn = "signed_in";

    /// <summary>A sign-in finished: the bridge has tokens and has connected.</summary>
    public const string Completed = "completed";

    /// <summary>A sign-in failed (refused, rejected by the server, or the code exchange failed).</summary>
    public const string Failed = "failed";

    /// <summary>A sign-in waited longer than the bridge allows.</summary>
    public const string Expired = "expired";

    /// <summary>A sign-in was replaced by a newer one for the same server, or the bridge stopped.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// The server no longer uses OAuth sign-in: its entry changed to other auth or was removed.
    /// Frontends take its prompt down.
    /// </summary>
    public const string NotRequired = "not_required";

    /// <summary>Whether <paramref name="state"/> settles a sign-in.</summary>
    public static bool IsTerminal(string state) =>
        state is Completed or Failed or Expired or Cancelled;
}
