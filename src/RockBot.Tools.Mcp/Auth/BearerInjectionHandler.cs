using System.Net;
using System.Net.Http.Headers;

namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// HTTP message handler that attaches a bearer token from an
/// <see cref="ITokenProvider"/> to every outgoing request. On a 401 response,
/// it forces a token refresh and retries the request exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Two-attempt strategy:
/// </para>
/// <list type="number">
/// <item>First attempt uses a (possibly cached) token from <see cref="ITokenProvider.GetAccessTokenAsync"/>.</item>
/// <item>
/// On 401, calls <see cref="ITokenProvider.GetAccessTokenAsync"/> with
/// <c>forceRefresh: true</c> and retries once.
/// </item>
/// <item>If the second attempt also returns 401, throws <see cref="McpAuthChallengeException"/> carrying the parsed <c>WWW-Authenticate</c> challenge.</item>
/// </list>
/// <para>
/// The first attempt's response body is disposed before retry to free any
/// network resources; the second attempt's body is left intact for the caller.
/// </para>
/// </remarks>
public sealed class BearerInjectionHandler : DelegatingHandler
{
    private readonly ITokenProvider _tokenProvider;

    public BearerInjectionHandler(ITokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public BearerInjectionHandler(ITokenProvider tokenProvider, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await ApplyBearerAsync(request, forceRefresh: false, cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // First attempt failed with 401 — discard the response and retry once
        // with a forcibly refreshed token.
        response.Dispose();

        await ApplyBearerAsync(request, forceRefresh: true, cancellationToken);
        var retryResponse = await base.SendAsync(request, cancellationToken);

        if (retryResponse.StatusCode != HttpStatusCode.Unauthorized)
            return retryResponse;

        // Second 401 — give up and surface the parsed challenge so the caller
        // can include the protected-resource-metadata URL in its error message.
        var challengeHeader = retryResponse.Headers.WwwAuthenticate
            .Select(h => h.Parameter is null ? h.Scheme : $"{h.Scheme} {h.Parameter}")
            .FirstOrDefault();
        WwwAuthenticateChallenge.TryParse(challengeHeader, out var challenge);

        retryResponse.Dispose();

        var detail = challenge?.ResourceMetadata is { } meta
            ? $" The server's protected resource metadata is at {meta}."
            : string.Empty;
        var error = challenge?.Error is { } code
            ? $" ({code}: {challenge.ErrorDescription})"
            : string.Empty;

        throw new McpAuthChallengeException(
            $"MCP server rejected bearer token with 401 after a forced refresh.{error}{detail}",
            challenge);
    }

    private async Task ApplyBearerAsync(
        HttpRequestMessage request, bool forceRefresh, CancellationToken ct)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(forceRefresh, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
