using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RockBot.UserProxy.AgUi;

/// <summary>What an access ticket lets its holder fetch.</summary>
internal enum AccessTicketKind
{
    /// <summary>One attachment: <c>GET /t/{ticket}/attachments/{path}</c>.</summary>
    Attachment,

    /// <summary>One thread's notify stream: <c>GET /agui/notify?threadId=…&amp;ticket=…</c>.</summary>
    Notify,
}

/// <summary>What a valid ticket says.</summary>
/// <param name="Kind">What it is for.</param>
/// <param name="Scope">The attachment's path, or the thread id.</param>
/// <param name="Email">The account it was issued to, checked against the allowlist again when it is used.</param>
/// <param name="ExpiresAt">When the ticket stops being accepted.</param>
/// <param name="TokenExpiresAt">When the bearer token it was issued for expires: a stream opened with the ticket ends then.</param>
internal sealed record AccessGrant(
    AccessTicketKind Kind, string Scope, string Email, DateTimeOffset ExpiresAt, DateTimeOffset TokenExpiresAt)
{
    /// <summary>True when the ticket is for the notify stream of <paramref name="threadId"/>.</summary>
    public bool CoversNotify(string? threadId) =>
        Kind == AccessTicketKind.Notify && string.Equals(Scope, threadId, StringComparison.Ordinal);

    /// <summary>
    /// True when the ticket is for the attachment at <paramref name="path"/>: the one it names,
    /// or, when it names an HTML document in a directory, a file under that directory.
    /// </summary>
    /// <remarks>
    /// An HTML attachment (a deck, a report) refers to the images and scripts stored beside it by
    /// relative links, and a browser resolves those against the document's URL: the ticket in
    /// that URL is the only credential the requests for them carry. So the document's ticket
    /// covers its own directory. Only HTML gets that, and only below a directory: a ticket for a
    /// file at the root of the attachments directory, or for an image anywhere, is for that file.
    /// </remarks>
    public bool CoversAttachment(string? path)
    {
        if (Kind != AccessTicketKind.Attachment || path is null || !AttachmentPathResolver.IsSafeRelativePath(path))
            return false;
        if (string.Equals(Scope, path, StringComparison.Ordinal))
            return true;

        var slash = Scope.LastIndexOf('/');
        return slash > 0
            && AttachmentMimeTypes.FromFileName(Scope) == AttachmentMimeTypes.Html
            && path.StartsWith(Scope[..(slash + 1)], StringComparison.Ordinal);
    }
}

/// <summary>
/// Issues and checks access tickets (#794, phase 5): short-lived, signed, single-purpose
/// credentials for the two requests a browser makes by itself and so can't put an
/// <c>Authorization</c> header on. One is a navigation or an <c>&lt;img src&gt;</c> to an
/// attachment; the other is an <c>EventSource</c> on the notify stream.
/// </summary>
/// <remarks>
/// <para>
/// A client that holds a bearer token asks for a ticket (<c>POST /tickets</c>) for one attachment
/// or one thread's notify stream, and gets back a URL that carries it. The ticket is good for
/// minutes (<see cref="AgUiAuthOptions.TicketLifetime"/>), for <c>GET</c> on that one resource,
/// and for nothing else: it is not a token, and the bearer token itself never appears in a URL,
/// where it would end up in history, logs and <c>Referer</c> headers.
/// </para>
/// <para>
/// The alternative of signing the <c>url</c> in <c>rockbot.attachments</c> events was rejected:
/// events are replayed from the notify buffer for a day and from history for ever, so a URL in
/// one would have to stay valid as long, which is a long-lived credential in a URL after all.
/// </para>
/// <para>
/// A ticket is the payload and an HMAC-SHA256 over it; nothing is stored. The key is random per
/// process unless <see cref="AgUiAuthOptions.TicketKey"/> sets one, so by default a restart, or
/// another replica, refuses the tickets in flight and the client asks for new ones.
/// </para>
/// </remarks>
internal sealed class AccessTicketService
{
    private const string Version = "v1";

    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public AccessTicketService(byte[]? key = null, TimeProvider? clock = null)
    {
        _key = key ?? RandomNumberGenerator.GetBytes(32);
        _clock = clock ?? TimeProvider.System;
    }

    private sealed record Payload(
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("s")] string Scope,
        [property: JsonPropertyName("m")] string Email,
        [property: JsonPropertyName("e")] long Expires,
        [property: JsonPropertyName("x")] long TokenExpires);

    /// <summary>
    /// A ticket for <paramref name="scope"/>, good for <paramref name="lifetime"/> and never
    /// beyond <paramref name="tokenExpiresAt"/>, the expiry of the token it is issued for.
    /// </summary>
    public (string Ticket, DateTimeOffset ExpiresAt) Issue(
        AccessTicketKind kind, string scope, string email, DateTimeOffset tokenExpiresAt, TimeSpan lifetime)
    {
        var expires = _clock.GetUtcNow() + lifetime;
        if (expires > tokenExpiresAt)
            expires = tokenExpiresAt;
        // Whole seconds, as the ticket carries them.
        expires = DateTimeOffset.FromUnixTimeSeconds(expires.ToUnixTimeSeconds());

        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(
            kind == AccessTicketKind.Notify ? "n" : "a", scope, email,
            expires.ToUnixTimeSeconds(), tokenExpiresAt.ToUnixTimeSeconds())));
        var signed = $"{Version}.{payload}";
        return ($"{signed}.{Base64Url.EncodeToString(Sign(signed))}", expires);
    }

    /// <summary>What <paramref name="ticket"/> grants, or null when it is malformed, not signed by this proxy, or expired.</summary>
    public AccessGrant? Validate(string? ticket)
    {
        // Longer than any ticket this service issues: not worth the hash.
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 2048)
            return null;

        var cut = ticket.LastIndexOf('.');
        if (cut <= Version.Length + 1 || !ticket.StartsWith(Version + ".", StringComparison.Ordinal))
            return null;

        var signed = ticket[..cut];
        Payload? payload;
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Base64Url.DecodeFromChars(ticket.AsSpan(cut + 1)), Sign(signed)))
                return null;
            payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(signed.AsSpan(Version.Length + 1)));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }

        if (payload is not { Kind: "a" or "n", Scope.Length: > 0, Email.Length: > 0 })
            return null;

        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.Expires);
        if (expires <= _clock.GetUtcNow())
            return null;

        return new AccessGrant(
            payload.Kind == "n" ? AccessTicketKind.Notify : AccessTicketKind.Attachment,
            payload.Scope, payload.Email, expires, DateTimeOffset.FromUnixTimeSeconds(payload.TokenExpires));
    }

    private byte[] Sign(string signed) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(signed));
}
