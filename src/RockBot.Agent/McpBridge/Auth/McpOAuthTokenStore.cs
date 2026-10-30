using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;

namespace RockBot.Agent.McpBridge.Auth;

/// <summary>
/// Durable OAuth tokens for MCP servers the user signed in to (#738): one file per server, so the
/// bridge refreshes after a restart without asking the user again. The SDK's OAuth provider reads
/// and writes them through the <see cref="ITokenCache"/> each server's transport is given.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Keyed by server, not by name.</b> A file belongs to the server's URL and client id,
///   so renaming the entry in <c>mcp.json</c> or changing its tool filters keeps the sign-in,
///   while pointing it at another server or client starts over.</item>
///   <item><b>Secret.</b> A file holds a refresh token (and a registered client's secret). It is
///   written owner-only and never logged.</item>
///   <item><b>Crash-safe.</b> Each write goes to a temporary file renamed over the old one.</item>
///   <item><b>Versioned.</b> The file carries a top-level <c>version</c> (see
///   <c>design/schema-migrations.md</c>).</item>
///   <item><b>Unreadable is signed out.</b> A file that can't be read is copied aside as
///   <c>.corrupt</c> and the server needs a new sign-in.</item>
/// </list>
/// </remarks>
internal sealed class McpOAuthTokenStore(string directory, ILogger logger)
{
    /// <summary>The file layout this build writes.</summary>
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>Where the token files live.</summary>
    public string Directory => directory;

    /// <summary>The durable cache for a server's normal connections.</summary>
    public ITokenCache CacheFor(string serverUrl, string? clientId) => SlotFor(serverUrl, clientId);

    /// <summary>
    /// A cache for a sign-in the user started: it reports no tokens until the sign-in stores its
    /// own, so the SDK runs the whole authorization flow rather than reusing the old sign-in, and
    /// what the sign-in stores replaces the old tokens for every later connection.
    /// </summary>
    public ITokenCache FreshCacheFor(string serverUrl, string? clientId) => new FreshView(SlotFor(serverUrl, clientId));

    /// <summary>Whether the bridge holds tokens for the server.</summary>
    public async Task<bool> HasTokensAsync(string serverUrl, string? clientId, CancellationToken ct = default) =>
        await SlotFor(serverUrl, clientId).GetTokensAsync(ct) is not null;

    /// <summary>
    /// The file name for a server: a hash, since a URL can't be a file name and the name must not
    /// reveal which servers the user signs in to.
    /// </summary>
    internal static string KeyFor(string serverUrl, string? clientId)
    {
        var identity = McpBridgeServerConfig.NormalizeUrl(serverUrl) + "\n" + (clientId?.Trim() ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }

    private Slot SlotFor(string serverUrl, string? clientId)
    {
        var key = KeyFor(serverUrl, clientId);
        return _slots.GetOrAdd(key, k => new Slot(Path.Combine(directory, k + ".json"), serverUrl, logger));
    }

    /// <summary>One server's tokens: read from disk once, then served from memory.</summary>
    private sealed class Slot(string path, string serverUrl, ILogger logger) : ITokenCache
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private TokenContainer? _tokens;
        private bool _loaded;

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            // Called on every request, concurrently: only the first read takes the lock.
            if (Volatile.Read(ref _loaded))
                return _tokens;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_loaded)
                {
                    _tokens = Load();
                    Volatile.Write(ref _loaded, true);
                }
                return _tokens;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // On disk first, so memory never claims a sign-in a restart would lose.
                Write(tokens);
                _tokens = tokens;
                Volatile.Write(ref _loaded, true);
            }
            finally
            {
                _gate.Release();
            }
        }

        private TokenContainer? Load()
        {
            if (!File.Exists(path))
                return null;

            try
            {
                var file = JsonSerializer.Deserialize<TokenFile>(File.ReadAllText(path), JsonOptions);
                if (file?.Version > CurrentVersion)
                {
                    logger.LogWarning(
                        "MCP OAuth token file {Path} is version {Version}, newer than this build's {Current}; reading it as is",
                        path, file.Version, CurrentVersion);
                }
                return file?.Tokens is { AccessToken.Length: > 0 } tokens ? tokens : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // The server needs a new sign-in; keep the file for a person to look at.
                logger.LogError(ex,
                    "MCP OAuth token file {Path} for {Server} could not be read; the server needs a new sign-in",
                    path, serverUrl);
                try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* Best effort. */ }
                return null;
            }
        }

        private void Write(TokenContainer tokens)
        {
            var dir = Path.GetDirectoryName(path)!;
            CreateOwnerOnlyDirectory(dir);

            var json = JsonSerializer.Serialize(
                new TokenFile { Version = CurrentVersion, ServerUrl = serverUrl, Tokens = tokens }, JsonOptions);
            var temp = path + ".tmp";

            // Owner-only from the moment it exists: the refresh token is never readable by others,
            // not even between create and chmod.
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write(json);

            File.Move(temp, path, overwrite: true);
        }

        private static void CreateOwnerOnlyDirectory(string dir)
        {
            if (System.IO.Directory.Exists(dir))
                return;
            if (OperatingSystem.IsWindows())
                System.IO.Directory.CreateDirectory(dir);
            else
                System.IO.Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>See <see cref="FreshCacheFor"/>.</summary>
    private sealed class FreshView(Slot slot) : ITokenCache
    {
        private volatile bool _stored;

        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
            _stored ? slot.GetTokensAsync(cancellationToken) : ValueTask.FromResult<TokenContainer?>(null);

        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            await slot.StoreTokensAsync(tokens, cancellationToken);
            _stored = true;
        }
    }

    private sealed class TokenFile
    {
        public int Version { get; set; }

        /// <summary>The server the tokens are for, so a person can tell the files apart.</summary>
        public string? ServerUrl { get; set; }

        public TokenContainer? Tokens { get; set; }
    }
}
