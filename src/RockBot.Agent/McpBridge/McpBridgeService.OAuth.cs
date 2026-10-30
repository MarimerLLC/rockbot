using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using RockBot.Agent.McpBridge.Auth;
using RockBot.Messaging;
using RockBot.Tools.Mcp.Auth;

namespace RockBot.Agent.McpBridge;

// MCP OAuth sign-in (#738). A server with "auth": { "oauth": … } is reached through the SDK's own
// OAuth client: PRM and authorization-server discovery, client registration, PKCE, the code
// exchange and refresh all happen here, with tokens kept in McpOAuthTokenStore. The one step the
// bridge can't do is the user's browser, so the authorization callback has two modes:
//
//  * Background — every normal connect and tool call. It never waits for a person: it marks the
//    server "sign-in required", tells the user interfaces, and fails the call with auth_required.
//  * Sign-in — a dedicated connect a user interface starts (McpSignInStartRequest). Its callback
//    hands the authorization URL back to that interface and waits for the redirect's code, which
//    any interface relays (McpSignInCallback). The PKCE verifier never leaves the SDK, so the code
//    is all a user interface ever sees.
//
// See design/mcp-oauth.md.
public sealed partial class McpBridgeService
{
    /// <summary>
    /// Stands in for a redirect URI on connections that never send anyone to the authorization
    /// server: the background callback fails before a browser would see it.
    /// </summary>
    private static readonly Uri BackgroundRedirectUri = new("http://127.0.0.1/mcp-oauth/callback");

    /// <summary>How long a start request waits for the SDK to reach the authorization step.</summary>
    private static readonly TimeSpan SignInChallengeWait = TimeSpan.FromSeconds(60);

    private McpOAuthTokenStore? _oauthTokens;

    /// <summary>Servers that rejected the agent until the user signs in, with the message given out.</summary>
    private readonly ConcurrentDictionary<string, string> _signInRequired = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sign-in under way for each server; a new one replaces it.</summary>
    private readonly ConcurrentDictionary<string, PendingSignIn> _signInsByServer = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same sign-ins, by the <c>state</c> their authorization URL carries.</summary>
    private readonly ConcurrentDictionary<string, PendingSignIn> _signInsByState = new(StringComparer.Ordinal);

    private ISubscription? _signInSubscription;

    /// <summary>Topic user interfaces send sign-in requests to.</summary>
    private string SignInTopic => $"{UserProxy.UserProxyTopics.McpSignIn}.{_agentName}";

    private McpOAuthTokenStore OAuthTokens => LazyInitializer.EnsureInitialized(ref _oauthTokens, () =>
        new McpOAuthTokenStore(
            Path.IsPathRooted(_options.OAuthTokenDirectory)
                ? _options.OAuthTokenDirectory
                : Path.Combine(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory, _options.OAuthTokenDirectory),
            _logger));

    /// <summary>Whether the server needs the user to sign in before the bridge can reach it.</summary>
    internal bool IsSignInRequired(string serverName) => _signInRequired.ContainsKey(serverName);

    private async Task StartSignInHandlingAsync(CancellationToken ct)
    {
        _signInSubscription = await _subscriber.SubscribeAsync(
            SignInTopic,
            $"mcp-bridge.{_agentName}.signin",
            HandleSignInRequestAsync,
            ct);
    }

    private async Task StopSignInHandlingAsync()
    {
        foreach (var pending in _signInsByServer.Values)
            pending.Cancel();

        if (_signInSubscription is not null)
            await _signInSubscription.DisposeAsync();
        _signInSubscription = null;
    }

    /// <summary>
    /// The SDK OAuth options for a server's transport. <paramref name="signIn"/> is the sign-in a
    /// user started, for its dedicated connect; null for every normal connection.
    /// </summary>
    private ClientOAuthOptions BuildOAuthOptions(string serverName, McpBridgeServerConfig config, PendingSignIn? signIn)
    {
        var oauth = config.Auth!.OAuth!;
        var clientId = NullIfBlank(oauth.ClientId);
        var secret = oauth.ClientSecret is null ? null : NullIfBlank(ExpandEnvVars(oauth.ClientSecret));

        var options = new ClientOAuthOptions
        {
            RedirectUri = signIn?.RedirectUri ?? BackgroundRedirectUri,
            ClientId = clientId,
            ClientSecret = secret,
            ClientMetadataDocumentUri = NullIfBlank(oauth.ClientMetadataDocumentUri) is { } cimd ? new Uri(cimd) : null,
            DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = $"RockBot ({_agentName})" },
            TokenCache = signIn is null
                ? OAuthTokens.CacheFor(config.Url!, clientId)
                : OAuthTokens.FreshCacheFor(config.Url!, clientId),
            AuthorizationCallbackHandler = signIn is null
                ? (_, _) => SignInRequiredAsync(serverName)
                : signIn.WaitForUserAsync,
        };

        // Fallbacks only: the server's 401 and its resource metadata name scopes first.
        if (oauth.Scopes.Count > 0)
            options.Scopes = [.. oauth.Scopes];
        if (oauth.AdditionalAuthorizationParameters.Count > 0)
            options.AdditionalAuthorizationParameters = new Dictionary<string, string>(oauth.AdditionalAuthorizationParameters);
        return options;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The background callback: the server wants the user to sign in, and nobody is waiting at a
    /// browser. Fails the connect or call with <see cref="TokenAcquisitionException"/>, which the
    /// invoke path turns into an <c>auth_required</c> tool error carrying this message.
    /// </summary>
    private Task<AuthorizationResult?> SignInRequiredAsync(string serverName)
    {
        var message = DescribeSignInRequired(serverName);
        MarkSignInRequired(serverName, message);
        return Task.FromException<AuthorizationResult?>(
            new TokenAcquisitionException(TokenAcquisitionException.Codes.NotAuthenticated, message));
    }

    internal static string DescribeSignInRequired(string serverName) =>
        $"The MCP server \"{serverName}\" needs the user to sign in before its tools can be used. " +
        $"Ask the user to sign in to \"{serverName}\" from a RockBot app — the sign-in prompt in the web chat, " +
        $"or `rockbot auth mcp {serverName}` in the CLI — then try again. Retrying before that will fail the same way.";

    private void MarkSignInRequired(string serverName, string message)
    {
        if (!_signInRequired.TryAdd(serverName, message))
            return;

        _logger.LogWarning("MCP server {Name} needs the user to sign in (OAuth); it stays unavailable until they do", serverName);
        _ = PublishSignInStatusAsync(serverName, UserProxy.McpSignInState.Required, signInId: null, message);
    }

    /// <summary>A connect to an OAuth server succeeded: whatever sign-in it needed is done.</summary>
    private void ClearSignInRequired(string serverName)
    {
        if (_signInRequired.TryRemove(serverName, out _))
            _ = PublishSignInStatusAsync(serverName, UserProxy.McpSignInState.SignedIn, signInId: null, message: null);
    }

    private async Task<MessageResult> HandleSignInRequestAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        try
        {
            if (envelope.MessageType == typeof(UserProxy.McpSignInStartRequest).FullName)
            {
                if (envelope.GetPayload<UserProxy.McpSignInStartRequest>() is not { } start)
                    return MessageResult.DeadLetter;
                var response = await StartSignInAsync(start, ct);
                await ReplyAsync(envelope, response, ct);
            }
            else if (envelope.MessageType == typeof(UserProxy.McpSignInCallback).FullName)
            {
                if (envelope.GetPayload<UserProxy.McpSignInCallback>() is not { } callback)
                    return MessageResult.DeadLetter;
                AcceptSignInCallback(callback);
            }
            else if (envelope.MessageType == typeof(UserProxy.McpSignInListRequest).FullName)
            {
                await ReplyAsync(envelope, await ListSignInsAsync(ct), ct);
            }
            else
            {
                _logger.LogWarning("Unexpected message type {Type} on {Topic}", envelope.MessageType, SignInTopic);
                return MessageResult.DeadLetter;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to handle MCP sign-in message {Type}", envelope.MessageType);
        }

        return MessageResult.Ack;
    }

    private async Task ReplyAsync<T>(MessageEnvelope request, T payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.ReplyTo))
            return;
        await _publisher.PublishAsync(request.ReplyTo,
            payload.ToEnvelope(source: $"mcp-bridge.{_agentName}", correlationId: request.CorrelationId), ct);
    }

    /// <summary>
    /// Starts a sign-in to a server and returns once the SDK has produced the authorization URL
    /// for the user (or failed before it could). The sign-in itself runs on, until the user's
    /// browser comes back or it expires.
    /// </summary>
    internal async Task<UserProxy.McpSignInStartResponse> StartSignInAsync(
        UserProxy.McpSignInStartRequest request, CancellationToken ct)
    {
        var name = request.ServerName;
        UserProxy.McpSignInStartResponse Refuse(string error) => new() { ServerName = name, Error = error };

        if (!_connections.TryGetConfig(name, out var config))
            return Refuse($"There is no MCP server named \"{name}\".");
        if (config.Auth?.OAuth is null)
            return Refuse($"The MCP server \"{name}\" doesn't use OAuth sign-in.");
        if (config.Auth.Validate() is { } authError)
            return Refuse($"The MCP server \"{name}\" has an unusable auth block: {authError}.");
        if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirectUri)
            || redirectUri.Scheme is not ("http" or "https"))
            return Refuse($"The redirect URI \"{request.RedirectUri}\" is not an absolute http(s) URL.");

        var timeout = TimeSpan.FromSeconds(Math.Max(30, _options.OAuthSignInTimeoutSeconds));
        var signIn = new PendingSignIn(name, redirectUri, DateTimeOffset.UtcNow + timeout, this);

        // One sign-in per server: the user may start over, and the old browser tab then goes nowhere.
        if (_signInsByServer.TryGetValue(name, out var previous))
            previous.Cancel();
        _signInsByServer[name] = signIn;

        _logger.LogInformation("Starting OAuth sign-in {SignInId} to MCP server {Name}", signIn.Id, name);
        var run = Task.Run(() => RunSignInAsync(signIn, config, timeout), CancellationToken.None);

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(SignInChallengeWait);
        try
        {
            await Task.WhenAny(signIn.Challenge, run).WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            signIn.Cancel();
            return Refuse($"The MCP server \"{name}\" didn't lead to a sign-in page within {SignInChallengeWait.TotalSeconds:0} seconds.");
        }

        if (signIn.Challenge.IsCompletedSuccessfully)
        {
            return new UserProxy.McpSignInStartResponse
            {
                ServerName = name,
                SignInId = signIn.Id,
                AuthorizationUri = signIn.Challenge.Result.AbsoluteUri,
                ExpiresAt = signIn.ExpiresAt,
            };
        }

        // The sign-in ended before any authorization step: it failed, or the server let the
        // agent in without one.
        return await run is { } failure
            ? Refuse(failure)
            : new UserProxy.McpSignInStartResponse { ServerName = name, SignInId = signIn.Id };
    }

    /// <summary>
    /// Runs one sign-in's dedicated connect. Returns null when it succeeded, or why it failed.
    /// Every outcome is also broadcast as an <see cref="UserProxy.McpSignInStatus"/>.
    /// </summary>
    private async Task<string?> RunSignInAsync(PendingSignIn signIn, McpBridgeServerConfig config, TimeSpan timeout)
    {
        var name = signIn.ServerName;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, signIn.Cancellation);
        cts.CancelAfter(timeout);

        string state;
        string? failure = null;
        try
        {
            var transport = CreateTransport(name, config, BuildOAuthOptions(name, config, signIn));
            // The connect is only there to drive the SDK through authorization; the tokens it stores
            // are what matters. The normal connect below opens the connection that serves calls.
            await using (await McpClient.CreateAsync(transport, new McpClientOptions(), cancellationToken: cts.Token))
            {
            }
            state = UserProxy.McpSignInState.Completed;
        }
        catch (OperationCanceledException) when (signIn.Cancellation.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            state = UserProxy.McpSignInState.Cancelled;
            failure = "The sign-in was replaced by a newer one, or the agent stopped.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            state = UserProxy.McpSignInState.Expired;
            failure = $"The sign-in wasn't finished within {timeout.TotalMinutes:0} minutes. Start it again.";
        }
        catch (Exception ex)
        {
            state = UserProxy.McpSignInState.Failed;
            failure = FindSignInRefused(ex)?.Message ?? $"The sign-in failed: {ex.Message}";
            _logger.LogWarning(ex, "OAuth sign-in {SignInId} to MCP server {Name} failed", signIn.Id, name);
        }
        finally
        {
            signIn.Settle();
            _signInsByServer.TryRemove(new KeyValuePair<string, PendingSignIn>(name, signIn));
        }

        if (failure is not null)
        {
            if (state != UserProxy.McpSignInState.Cancelled)
                _logger.LogInformation("OAuth sign-in {SignInId} to MCP server {Name} ended {State}: {Reason}",
                    signIn.Id, name, state, failure);
            await PublishSignInStatusAsync(name, state, signIn.Id, failure);
            return failure;
        }

        _logger.LogInformation("OAuth sign-in {SignInId} to MCP server {Name} completed; connecting", signIn.Id, name);
        _signInRequired.TryRemove(name, out _);
        await PublishSignInStatusAsync(name, state, signIn.Id, message: null);

        // The entry may have changed while the user was signing in; connect with the one in force.
        if (_connections.TryGetConfig(name, out var current))
        {
            try
            {
                await ConnectServerAsync(name, current, _lifetime.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "MCP server {Name} could not be connected after its sign-in", name);
            }
        }
        return null;
    }

    /// <summary>A user interface relayed the authorization server's redirect.</summary>
    internal void AcceptSignInCallback(UserProxy.McpSignInCallback callback)
    {
        if (!_signInsByState.TryGetValue(callback.State, out var signIn))
        {
            // A stale tab, a sign-in that already ended, or a forgery: nothing to give it to.
            _logger.LogWarning("Ignoring an OAuth sign-in callback that matches no sign-in under way");
            return;
        }

        if (callback.Error is { Length: > 0 } error)
        {
            var detail = string.IsNullOrWhiteSpace(callback.ErrorDescription) ? error : $"{error}: {callback.ErrorDescription}";
            signIn.Fail(new McpSignInRefusedException(
                $"The sign-in to \"{signIn.ServerName}\" was refused by its authorization server ({detail})."));
        }
        else if (string.IsNullOrEmpty(callback.Code))
        {
            signIn.Fail(new McpSignInRefusedException(
                $"The sign-in to \"{signIn.ServerName}\" came back without an authorization code."));
        }
        else
        {
            signIn.Complete(new AuthorizationResult { Code = callback.Code, State = callback.State, Iss = callback.Iss });
        }
    }

    internal async Task<UserProxy.McpSignInListResponse> ListSignInsAsync(CancellationToken ct)
    {
        var servers = new List<UserProxy.McpSignInServer>();
        foreach (var name in _connections.ConfiguredNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (!_connections.TryGetConfig(name, out var config) || config.Auth?.OAuth is not { } oauth
                || string.IsNullOrEmpty(config.Url))
                continue;

            var state = _signInsByServer.ContainsKey(name) ? UserProxy.McpSignInState.Pending
                : _signInRequired.ContainsKey(name) ? UserProxy.McpSignInState.Required
                : await OAuthTokens.HasTokensAsync(config.Url, NullIfBlank(oauth.ClientId), ct) ? UserProxy.McpSignInState.SignedIn
                : UserProxy.McpSignInState.Required;

            servers.Add(new UserProxy.McpSignInServer
            {
                ServerName = name,
                State = state,
                Connected = _connections.IsConnected(name),
            });
        }
        return new UserProxy.McpSignInListResponse { Servers = servers };
    }

    private async Task PublishSignInStatusAsync(string serverName, string state, string? signInId, string? message)
    {
        try
        {
            await _publisher.PublishAsync($"{UserProxy.UserProxyTopics.UserResponse}.{_agentName}",
                new UserProxy.McpSignInStatus
                {
                    ServerName = serverName,
                    State = state,
                    SignInId = signInId,
                    Message = message,
                }.ToEnvelope<UserProxy.McpSignInStatus>(source: _agentName), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell the user interfaces that MCP server {Name}'s sign-in is {State}",
                serverName, state);
        }
    }

    private static McpSignInRefusedException? FindSignInRefused(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is McpSignInRefusedException refused) return refused;
        }
        return null;
    }

    /// <summary>The user or the authorization server refused a sign-in.</summary>
    private sealed class McpSignInRefusedException(string message) : Exception(message);

    /// <summary>A sign-in a user started, from its authorization URL to the redirect's code.</summary>
    private sealed class PendingSignIn(string serverName, Uri redirectUri, DateTimeOffset expiresAt, McpBridgeService bridge)
    {
        private readonly TaskCompletionSource<Uri> _challenge = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AuthorizationResult> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cancel = new();
        private string? _state;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string ServerName => serverName;
        public Uri RedirectUri => redirectUri;
        public DateTimeOffset ExpiresAt => expiresAt;

        /// <summary>The authorization URL, once the SDK has reached that step.</summary>
        public Task<Uri> Challenge => _challenge.Task;

        public CancellationToken Cancellation => _cancel.Token;

        /// <summary>The sign-in callback the SDK calls with the authorization URL.</summary>
        public async Task<AuthorizationResult?> WaitForUserAsync(AuthorizationCallbackContext context, CancellationToken ct)
        {
            // One authorization per sign-in: the user interface was handed one URL to open.
            if (_challenge.Task.IsCompleted)
                throw new McpSignInRefusedException(
                    $"The MCP server \"{serverName}\" asked for a second authorization during one sign-in; start the sign-in again.");

            var state = ReadQueryParameter(context.AuthorizationUri, "state")
                        ?? throw new McpSignInRefusedException(
                            $"The authorization URL for \"{serverName}\" carries no state, so its redirect couldn't be matched.");

            _state = state;
            bridge._signInsByState[state] = this;
            _challenge.TrySetResult(context.AuthorizationUri);

            return await _callback.Task.WaitAsync(ct);
        }

        public void Complete(AuthorizationResult result) => _callback.TrySetResult(result);

        public void Fail(Exception ex) => _callback.TrySetException(ex);

        public void Cancel()
        {
            try { _cancel.Cancel(); } catch (ObjectDisposedException) { /* already settled */ }
        }

        /// <summary>The sign-in ended: its redirect no longer goes anywhere.</summary>
        public void Settle()
        {
            if (_state is not null)
                bridge._signInsByState.TryRemove(new KeyValuePair<string, PendingSignIn>(_state, this));
            _challenge.TrySetCanceled();
        }

        private static string? ReadQueryParameter(Uri uri, string name)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];
                if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
                    return separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            }
            return null;
        }
    }
}
