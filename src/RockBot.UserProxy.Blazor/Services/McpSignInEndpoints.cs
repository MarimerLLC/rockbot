using System.Net;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// The browser leg of MCP OAuth sign-in (#738): the redirect URI an authorization server sends
/// the user back to. The page only relays what it was given — the code, state and issuer, or the
/// refusal — to the agent, which holds the PKCE verifier and exchanges the code itself. The code
/// alone is worthless, so nothing here is a credential.
/// </summary>
public static class McpSignInEndpoints
{
    /// <summary>The redirect path, relative to the app's base URL.</summary>
    public const string CallbackPath = "/mcp-oauth/callback";

    /// <summary>Maps <see cref="CallbackPath"/>.</summary>
    public static IEndpointConventionBuilder MapMcpSignInCallback(this IEndpointRouteBuilder app) =>
        app.MapGet(CallbackPath, async (HttpRequest request, UserProxyService proxy, CancellationToken ct) =>
        {
            var query = request.Query;
            var state = query["state"].ToString();
            if (string.IsNullOrEmpty(state))
                return Page(HttpStatusCode.BadRequest, "This sign-in link is incomplete",
                    "The authorization server sent the browser back without a state, so the sign-in it belongs to can't be found. Start the sign-in again.");

            var callback = new McpSignInCallback
            {
                State = state,
                Code = NullIfEmpty(query["code"].ToString()),
                Iss = NullIfEmpty(query["iss"].ToString()),
                Error = NullIfEmpty(query["error"].ToString()),
                ErrorDescription = NullIfEmpty(query["error_description"].ToString()),
            };
            await proxy.SendMcpSignInCallbackAsync(callback, ct);

            return callback.Error is null
                ? Page(HttpStatusCode.OK, "Signed in",
                    "RockBot is finishing the sign-in. You can close this tab and go back to the chat.")
                : Page(HttpStatusCode.OK, "Sign-in refused",
                    $"The authorization server refused the sign-in ({callback.Error}). You can close this tab; the chat shows what happened.");
        })
        // A side door around the app's own sign-in otherwise: anyone could feed codes to the agent.
        // The default policy is permissive when Auth:Enabled=false, as for /attachments.
        .RequireAuthorization();

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static IResult Page(HttpStatusCode status, string title, string message) =>
        Results.Content(
            $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>body{font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem;color:#222}</style></head>
            <body><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p></body>
            </html>
            """,
            "text/html; charset=utf-8",
            statusCode: (int)status);
}
