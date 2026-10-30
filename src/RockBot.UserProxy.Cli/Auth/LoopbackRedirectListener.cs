using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace RockBot.UserProxy.Cli.Auth;

/// <summary>
/// The CLI's redirect URI for MCP OAuth sign-in (#738): a one-shot HTTP listener on the loopback
/// interface (RFC 8252 §7.3), which the authorization server sends the user's browser back to.
/// It reads the redirect's query and answers with a page telling the user to return to the
/// terminal. A raw socket rather than <see cref="HttpListener"/>, which on Windows needs a URL
/// reservation to listen on <c>127.0.0.1</c>.
/// </summary>
internal sealed class LoopbackRedirectListener : IDisposable
{
    private const string CallbackPath = "/callback";
    private readonly TcpListener _listener;

    private LoopbackRedirectListener(TcpListener listener)
    {
        _listener = listener;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RedirectUri = $"http://127.0.0.1:{port}{CallbackPath}";
    }

    /// <summary>The redirect URI to hand the agent.</summary>
    public string RedirectUri { get; }

    /// <summary>Listens on <paramref name="port"/>, or on a free port when it is 0.</summary>
    public static LoopbackRedirectListener Start(int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new LoopbackRedirectListener(listener);
    }

    /// <summary>
    /// Waits for the browser's redirect for the sign-in whose authorization URL carried
    /// <paramref name="expectedState"/>, and returns what it carried. Anything else — a browser's
    /// favicon fetch, a stale tab's redirect, a request with neither a code nor an error — is
    /// answered and the wait goes on, so it can't take the place of the real redirect.
    /// </summary>
    public async Task<McpSignInCallback> WaitAsync(string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(ct) ?? string.Empty;
            // Drain the headers; nothing in them matters.
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }

            var target = requestLine.Split(' ') is [_, var t, ..] ? t : string.Empty;
            var callback = target.StartsWith(CallbackPath, StringComparison.Ordinal)
                ? ParseRedirect("http://127.0.0.1" + target)
                : null;

            if (callback is null)
            {
                await RespondAsync(stream, 404, "Not found", "Nothing here.", ct);
                continue;
            }

            if (!string.Equals(callback.State, expectedState, StringComparison.Ordinal)
                || (callback.Code is null && callback.Error is null))
            {
                await RespondAsync(stream, 400, "Not this sign-in",
                    "This isn't the sign-in RockBot is waiting for. Use the link the terminal printed most recently.", ct);
                continue;
            }

            var refused = callback.Error is not null;
            await RespondAsync(stream, 200,
                refused ? "Sign-in refused" : "Signed in",
                refused
                    ? $"The authorization server refused the sign-in ({callback.Error}). Go back to the terminal."
                    : "RockBot is finishing the sign-in. You can close this tab and go back to the terminal.",
                ct);
            return callback;
        }
    }

    /// <summary>
    /// Reads a redirect URL — the listener's request, or one the user pasted from the browser's
    /// address bar — into a callback. Null when it carries no <c>state</c>, so it isn't a redirect.
    /// </summary>
    public static McpSignInCallback? ParseRedirect(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        var query = HttpUtility.ParseQueryString(uri.Query);
        if (string.IsNullOrEmpty(query["state"]))
            return null;

        return new McpSignInCallback
        {
            State = query["state"]!,
            Code = NullIfEmpty(query["code"]),
            Iss = NullIfEmpty(query["iss"]),
            Error = NullIfEmpty(query["error"]),
            ErrorDescription = NullIfEmpty(query["error_description"]),
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static async Task RespondAsync(Stream stream, int status, string title, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title></head>" +
            $"<body style=\"font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto\">" +
            $"<h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" }}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public void Dispose() => _listener.Stop();
}
