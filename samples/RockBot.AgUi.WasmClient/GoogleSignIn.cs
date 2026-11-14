using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace RockBot.AgUi.WasmClient;

/// <summary>
/// App mode's sign-in: Google Identity Services in the browser, through a few lines of JavaScript
/// (<c>wwwroot/js/googleSignIn.js</c>). Google hands the page an ID token (the
/// <c>credential</c>); the sample sends it to the proxy as the bearer token, which is all the
/// proxy asks for (<c>docs/agui.md</c>, "App auth").
/// </summary>
/// <remarks>
/// The token lives in memory only: a reload signs in again (silently, when Google still has the
/// session). An ID token lasts about an hour. This sample does not refresh it in the background:
/// when it has expired the sign-in button is shown again.
/// </remarks>
public sealed class GoogleSignIn(IJSRuntime js, ClientSettings settings) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<GoogleSignIn>? _self;
    private string? _token;
    private DateTimeOffset _expires;

    /// <summary>Raised when the user signs in or out, or the token expires.</summary>
    public event Action? Changed;

    public bool IsEnabled => settings.AuthEnabled;

    /// <summary>True while there is a token that hasn't expired.</summary>
    public bool IsSignedIn => _token is not null && DateTimeOffset.UtcNow < _expires;

    /// <summary>The address in the token, to show who is signed in.</summary>
    public string? Email { get; private set; }

    /// <summary>The bearer token for a request, or null when there is none to send.</summary>
    public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(IsSignedIn ? _token : null);

    /// <summary>Loads Google's script, draws its button in <paramref name="button"/> and offers one-tap sign-in.</summary>
    public async Task ShowAsync(ElementReference button)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/googleSignIn.js");
        _self ??= DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("show", settings.GoogleClientId, button, _self);
    }

    /// <summary>Called by the script with the ID token Google issued.</summary>
    [JSInvokable]
    public void OnCredential(string credential)
    {
        _token = credential;
        (_expires, Email) = Read(credential);
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        _token = null;
        Email = null;
        if (_module is not null)
            await _module.InvokeVoidAsync("signOut");
        Changed?.Invoke();
    }

    /// <summary>
    /// The expiry and address in a token's payload. Read for display and timing only: the proxy
    /// is what validates the token.
    /// </summary>
    internal static (DateTimeOffset Expires, string? Email) Read(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            var expires = json.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.UtcNow.AddMinutes(55);
            var email = json.RootElement.TryGetProperty("email", out var address) ? address.GetString() : null;
            // A minute early, so a request isn't sent with a token that expires on the way.
            return (expires.AddMinutes(-1), email);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException)
        {
            return (DateTimeOffset.UtcNow.AddMinutes(55), null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _self?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going away.
            }
        }
    }
}
