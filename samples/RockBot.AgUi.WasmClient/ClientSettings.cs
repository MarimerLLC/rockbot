using Microsoft.Extensions.Configuration;

namespace RockBot.AgUi.WasmClient;

/// <summary>
/// The sample's settings, from <c>wwwroot/appsettings.json</c> (section <c>AgUi</c>). The file is
/// served to the browser like any other static file, so nothing in it is secret: a Google client
/// id is public by design, and there is no client secret anywhere.
/// </summary>
public sealed record ClientSettings(Uri ProxyAddress, string ThreadId, bool AuthEnabled, string GoogleClientId)
{
    /// <summary>
    /// Reads the settings key by key: binding a section to a class needs reflection, which a
    /// trimmed WebAssembly app does without.
    /// </summary>
    public static ClientSettings From(IConfiguration configuration, string appBaseAddress)
    {
        // No proxy address means the app is served from the proxy's own origin (a reverse proxy
        // in front of both), so the API is where the page is.
        var baseUrl = configuration["AgUi:BaseUrl"] is { Length: > 0 } configured ? configured : appBaseAddress;
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        return new ClientSettings(
            new Uri(baseUrl, UriKind.Absolute),
            configuration["AgUi:ThreadId"] is { Length: > 0 } thread ? thread : "agui-session",
            string.Equals(configuration["AgUi:Auth:Enabled"], "true", StringComparison.OrdinalIgnoreCase),
            configuration["AgUi:Auth:GoogleClientId"] ?? "");
    }
}
