using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.A2A;

/// <summary>
/// Thread-safe agent directory with optional file persistence.
/// Entries are keyed by agent name and carry a last-seen timestamp so stale
/// registrations (agents that stopped without deregistering) can be pruned on
/// startup via <see cref="A2AOptions.DirectoryEntryTtl"/>.
///
/// Implements <see cref="IHostedService"/> to load the persisted file at startup
/// and flush on shutdown.
/// </summary>
internal sealed class AgentDirectory(
    A2AOptions options,
    ILogger<AgentDirectory> logger,
    IHttpClientFactory? httpClientFactory = null) : IAgentDirectory, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, AgentDirectoryEntry> _agents =
        new(StringComparer.OrdinalIgnoreCase);

    // Debounce: only one pending write at a time
    private volatile bool _writePending;

    // -------------------------------------------------------------------------
    // IHostedService

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Load persisted directory (if it exists)
        var path = ResolvePath(options.DirectoryPersistencePath);
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(json, JsonOptions);
                if (entries is not null)
                {
                    var cutoff = DateTimeOffset.UtcNow - options.DirectoryEntryTtl;
                    var loaded = 0;
                    var pruned = 0;

                    foreach (var e in entries)
                    {
                        if (e.Card is null) continue;

                        if (e.LastSeenAt < cutoff)
                        {
                            pruned++;
                            continue;
                        }

                        _agents[e.Card.AgentName] = new AgentDirectoryEntry
                        {
                            Card = e.Card,
                            LastSeenAt = e.LastSeenAt,
                            LlmSummary = e.LlmSummary
                        };
                        loaded++;
                    }

                    logger.LogInformation(
                        "Loaded {Loaded} agent(s) from directory ({Pruned} stale entries pruned, TTL={Ttl}h)",
                        loaded, pruned, options.DirectoryEntryTtl.TotalHours);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not load agent directory from {Path}", path);
            }
        }

        // Seed well-known agents — always runs, even when the persisted file doesn't
        // exist yet (first startup on a fresh volume).
        foreach (var card in options.WellKnownAgents)
        {
            if (_agents.TryGetValue(card.AgentName, out var existing))
            {
                _agents[card.AgentName] = existing with { IsWellKnown = true };
            }
            else
            {
                _agents[card.AgentName] = new AgentDirectoryEntry
                {
                    Card = card,
                    LastSeenAt = DateTimeOffset.MinValue,
                    IsWellKnown = true
                };
            }
            logger.LogInformation("Seeded well-known agent '{AgentName}'", card.AgentName);
        }

        // Enrich seeded well-known entries by fetching the peer's published
        // /.well-known/agent-card.json — the A2A-spec source of truth for
        // skills/description/version. Entries that already carry a skills array
        // in well-known-agents.json are treated as explicit overrides and left alone
        // (supports offline/airgapped deployments).
        await EnrichWellKnownFromRemoteAsync(cancellationToken);
    }

    private async Task EnrichWellKnownFromRemoteAsync(CancellationToken cancellationToken)
    {
        if (httpClientFactory is null) return;

        var toEnrich = options.WellKnownAgents
            .Where(c => !string.IsNullOrWhiteSpace(c.Url) && (c.Skills is null || c.Skills.Count == 0))
            .ToList();

        if (toEnrich.Count == 0) return;

        var tasks = toEnrich.Select(seed => FetchAndMergeAsync(seed, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task FetchAndMergeAsync(AgentCard seed, CancellationToken ct)
    {
        try
        {
            using var httpClient = httpClientFactory!.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            if (!string.IsNullOrEmpty(seed.AuthHeaderName) &&
                !string.IsNullOrEmpty(seed.AuthHeaderValueBase64))
            {
                var headerValue = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(seed.AuthHeaderValueBase64));
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(seed.AuthHeaderName, headerValue);
            }

            // Well-known is host-relative per RFC 8615 — resolve against the URL's
            // authority, not the (possibly path-prefixed) seed URL. Seeds like
            // "http://host/a2a/" must still fetch "http://host/.well-known/agent-card.json".
            if (!Uri.TryCreate(seed.Url, UriKind.Absolute, out var baseUri))
            {
                logger.LogWarning(
                    "Skipping enrichment for well-known peer '{AgentName}': invalid URL '{Url}'",
                    seed.AgentName, seed.Url);
                return;
            }

            var url = new Uri(baseUri, "/.well-known/agent-card.json").ToString();
            var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Could not fetch agent-card for well-known peer '{AgentName}' from {Url}: HTTP {Status}",
                    seed.AgentName, url, (int)response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct);

            // Extract via JsonDocument rather than deserializing as AgentCard — a v1
            // card uses "name" instead of "agentName" and would fail the required-field
            // check. The agent-card schema also varies between v0.3 and v1; pulling
            // fields explicitly keeps both paths working.
            var remote = ExtractRemoteFields(json);

            // Merge remote fields into the seeded card while preserving locally-configured
            // coordinates (Url, AuthHeader*) and the AgentName key.
            var merged = seed with
            {
                Description = remote.Description ?? seed.Description,
                Version = remote.Version ?? seed.Version,
                Skills = remote.Skills ?? seed.Skills,
                ProtocolVersion = remote.ProtocolVersion ?? seed.ProtocolVersion,
                SupportsStreaming = remote.SupportsStreaming ?? seed.SupportsStreaming
            };

            if (_agents.TryGetValue(seed.AgentName, out var existing))
            {
                _agents[seed.AgentName] = existing with { Card = merged };
                logger.LogInformation(
                    "Enriched well-known agent '{AgentName}' from {Url} (protocol={Protocol}, streaming={Streaming}, {SkillCount} skill(s))",
                    seed.AgentName, url, merged.ProtocolVersion ?? "(unset)",
                    merged.SupportsStreaming, merged.Skills?.Count ?? 0);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to enrich well-known agent '{AgentName}' from {Url} — entry kept without remote data",
                seed.AgentName, seed.Url);
        }
    }

    internal sealed record RemoteFields(
        string? Description,
        string? Version,
        IReadOnlyList<AgentSkill>? Skills,
        string? ProtocolVersion,
        bool? SupportsStreaming);

    /// <summary>
    /// Extracts the subset of agent-card fields we merge during enrichment, accepting
    /// both A2A v0.3 and v1 layouts. v1 collapses <c>protocolVersion</c>/<c>url</c>/
    /// <c>preferredTransport</c> into <c>supportedInterfaces[]</c> and stream support
    /// into <c>capabilities.streaming</c>; v0.3 keeps them at the top level. Using
    /// JsonDocument (rather than <c>JsonSerializer.Deserialize&lt;AgentCard&gt;</c>)
    /// avoids tripping on the v1 <c>name</c>-vs-<c>agentName</c> rename.
    /// </summary>
    internal static RemoteFields ExtractRemoteFields(string cardJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(cardJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, null, null, null, null);

            string? description = TryGetString(root, "description");
            string? version = TryGetString(root, "version");

            IReadOnlyList<AgentSkill>? skills = null;
            if (root.TryGetProperty("skills", out var skillsEl) &&
                skillsEl.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    skills = JsonSerializer.Deserialize<List<AgentSkill>>(skillsEl.GetRawText(), JsonOptions);
                }
                catch (JsonException) { /* leave skills null — partial enrichment is fine */ }
            }

            // protocolVersion: v0.3 uses top-level; v1 uses supportedInterfaces[].protocolVersion.
            string? protocolVersion = TryGetString(root, "protocolVersion");
            if (protocolVersion is null &&
                root.TryGetProperty("supportedInterfaces", out var interfaces) &&
                interfaces.ValueKind == JsonValueKind.Array)
            {
                foreach (var iface in interfaces.EnumerateArray())
                {
                    if (iface.ValueKind == JsonValueKind.Object)
                    {
                        var pv = TryGetString(iface, "protocolVersion");
                        if (pv is not null) { protocolVersion = pv; break; }
                    }
                }
            }

            // SupportsStreaming: v0.3 emits as a top-level bool; v1 nests under capabilities.
            bool? streaming = null;
            if (root.TryGetProperty("supportsStreaming", out var topStream))
            {
                if (topStream.ValueKind == JsonValueKind.True) streaming = true;
                else if (topStream.ValueKind == JsonValueKind.False) streaming = false;
            }
            if (streaming is null &&
                root.TryGetProperty("capabilities", out var caps) &&
                caps.ValueKind == JsonValueKind.Object &&
                caps.TryGetProperty("streaming", out var nested))
            {
                if (nested.ValueKind == JsonValueKind.True) streaming = true;
                else if (nested.ValueKind == JsonValueKind.False) streaming = false;
            }

            return new RemoteFields(description, version, skills, protocolVersion, streaming);
        }
        catch (JsonException)
        {
            return new RemoteFields(null, null, null, null, null);
        }
    }

    private static string? TryGetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public Task StopAsync(CancellationToken cancellationToken) =>
        FlushAsync(cancellationToken);

    // -------------------------------------------------------------------------
    // IAgentDirectory

    public AgentCard? GetAgent(string agentName) =>
        _agents.TryGetValue(agentName, out var entry) ? entry.Card : null;

    public IReadOnlyList<AgentCard> GetAllAgents() =>
        _agents.Values.Select(e => e.Card).ToList();

    public IReadOnlyList<AgentCard> FindBySkill(string skillId) =>
        _agents.Values
            .Where(e => e.Card.Skills?.Any(
                s => string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase)) == true)
            .Select(e => e.Card)
            .ToList();

    public IReadOnlyList<AgentDirectoryEntry> GetAllEntries() =>
        _agents.Values.ToList();

    // -------------------------------------------------------------------------
    // Write methods (called by AgentDiscoveryService)

    public void AddOrUpdate(AgentCard card)
    {
        // Preserve the IsWellKnown flag and existing LlmSummary if already set —
        // live announcements update the card and last-seen time but don't demote a
        // well-known agent or discard a previously-generated summary.
        var isWellKnown = _agents.TryGetValue(card.AgentName, out var existing) && existing.IsWellKnown;
        var existingSummary = existing?.LlmSummary;
        _agents[card.AgentName] = new AgentDirectoryEntry
        {
            Card = card,
            LastSeenAt = DateTimeOffset.UtcNow,
            IsWellKnown = isWellKnown,
            LlmSummary = existingSummary
        };
        ScheduleWrite();
    }

    public void SetSummary(string agentName, string summary)
    {
        if (!_agents.TryGetValue(agentName, out var existing)) return;
        _agents[agentName] = existing with { LlmSummary = summary };
        ScheduleWrite();
    }

    public void Remove(string agentName)
    {
        // Well-known agents are always callable — don't remove them from the directory
        // just because they sent a deregistration announcement (e.g. KEDA pod shutting down).
        if (_agents.TryGetValue(agentName, out var entry) && entry.IsWellKnown)
        {
            logger.LogInformation(
                "Ignoring deregistration for well-known agent '{AgentName}' — it remains callable",
                agentName);
            return;
        }

        if (_agents.TryRemove(agentName, out _))
        {
            logger.LogInformation("Removed deregistered agent '{AgentName}' from directory", agentName);
            ScheduleWrite();
        }
    }

    // -------------------------------------------------------------------------
    // Persistence helpers

    private void ScheduleWrite()
    {
        if (_writePending) return;
        _writePending = true;
        _ = Task.Run(async () =>
        {
            try { await FlushAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to persist agent directory"); }
            finally { _writePending = false; }
        });
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var path = ResolvePath(options.DirectoryPersistencePath);
        if (string.IsNullOrEmpty(path)) return;

        var entries = _agents.Values
            .Select(e => new PersistedEntry { Card = e.Card, LastSeenAt = e.LastSeenAt, LlmSummary = e.LlmSummary })
            .ToList();

        var json = JsonSerializer.Serialize(entries, JsonOptions);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, json, ct);

        logger.LogDebug("Persisted {Count} agent(s) to {Path}", entries.Count, path);
    }

    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(AppContext.BaseDirectory, path);
    }

    // DTO for JSON serialization — avoids polluting AgentDirectoryEntry with serializer concerns
    private sealed class PersistedEntry
    {
        public AgentCard? Card { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public string? LlmSummary { get; set; }
    }
}
