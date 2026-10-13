using System.Net;
using System.Net.Sockets;

namespace RockBot.Browser;

/// <summary>
/// Keeps rendered pages on the public internet. The sidecar shares the agent pod's network
/// namespace, so without this a page — or a script on it — could reach the agent's own
/// endpoints on localhost, other pods, or the cloud metadata service.
/// </summary>
internal sealed class UrlGuard(BrowserOptions options)
{
    /// <summary>
    /// Whether a request to <paramref name="uri"/> may proceed. Non-network schemes the
    /// browser handles internally (data:, blob:, about:) are allowed; other non-http(s)
    /// schemes are not. <paramref name="dnsCache"/> is per page, so a page's many subresource
    /// requests to one host cost one lookup.
    /// </summary>
    public async Task<bool> IsAllowedAsync(Uri uri, IDictionary<string, bool> dnsCache, CancellationToken ct)
    {
        if (uri.Scheme is "data" or "blob" or "about")
            return true;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (options.AllowPrivateNetworks)
            return true;

        var host = uri.IdnHost;
        if (dnsCache.TryGetValue(host, out var cached))
            return cached;

        bool allowed;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            allowed = IsPublic(literal);
        }
        else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                 host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            allowed = false;
        }
        else
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, ct);
                // Every address must be public: a name that resolves to one public and one
                // private address could be steered to the private one.
                allowed = addresses.Length > 0 && addresses.All(IsPublic);
            }
            catch (SocketException)
            {
                // Unresolvable: let the browser fail the request with its own error.
                allowed = true;
            }
        }

        dnsCache[host] = allowed;
        return allowed;
    }

    /// <summary>True for addresses on the public internet.</summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 ||                                   // 0.0.0.0/8
                     b[0] == 10 ||                                  // 10.0.0.0/8
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||  // 100.64.0.0/10 (CGNAT)
                     b[0] == 127 ||                                 // loopback
                     (b[0] == 169 && b[1] == 254) ||                // link-local, cloud metadata
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||   // 172.16.0.0/12
                     (b[0] == 192 && b[1] == 168) ||                // 192.168.0.0/16
                     b[0] >= 224);                                  // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal ||
                     address.IsIPv6SiteLocal ||
                     address.IsIPv6Multicast ||
                     (b[0] & 0xFE) == 0xFC);                        // fc00::/7 unique local
        }

        return false;
    }
}
