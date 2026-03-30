using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-backed knowledge graph store. Keeps entities and triples in memory
/// and persists them as JSON files on disk. Thread safety via <see cref="SemaphoreSlim"/>.
/// </summary>
internal sealed class FileKnowledgeGraphStore : IKnowledgeGraph
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _basePath;
    private readonly ILogger<FileKnowledgeGraphStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private Dictionary<string, KnowledgeEntity>? _entities;
    private Dictionary<string, KnowledgeTriple>? _triples;

    public FileKnowledgeGraphStore(
        IOptions<KnowledgeGraphOptions> graphOptions,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileKnowledgeGraphStore> logger)
    {
        _basePath = FileMemoryStore.ResolvePath(graphOptions.Value.BasePath, profileOptions.Value.BasePath);
        _logger = logger;

        Directory.CreateDirectory(_basePath);

        logger.LogInformation("Knowledge graph path: {Path}", _basePath);
    }

    // ── Entities ──────────────────────────────────────────────────────────────

    public async Task SaveEntityAsync(KnowledgeEntity entity, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (entities, _) = await EnsureIndexAsync(cancellationToken);

            var filePath = GetEntityFilePath(entity.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var json = JsonSerializer.Serialize(entity, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            entities[entity.Id] = entity;
            _logger.LogDebug("Saved knowledge entity {Id} ({Type}: {Name})", entity.Id, entity.EntityType, entity.Name);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<KnowledgeEntity?> GetEntityAsync(string id, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (entities, _) = await EnsureIndexAsync(cancellationToken);
            return entities.GetValueOrDefault(id);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeEntity>> FindEntitiesByNameAsync(string query, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (entities, _) = await EnsureIndexAsync(cancellationToken);

            return entities.Values
                .Where(e => MatchesName(e, query))
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeleteEntityAsync(string id, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (entities, triples) = await EnsureIndexAsync(cancellationToken);

            if (!entities.Remove(id))
                return;

            var filePath = GetEntityFilePath(id);
            if (File.Exists(filePath))
                File.Delete(filePath);

            // Remove all triples referencing this entity
            var toRemove = triples.Values
                .Where(t => t.Subject.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                            t.Object.Equals(id, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Id)
                .ToList();

            foreach (var tripleId in toRemove)
            {
                triples.Remove(tripleId);
                var tripleFile = GetTripleFilePath(tripleId);
                if (File.Exists(tripleFile))
                    File.Delete(tripleFile);
            }

            _logger.LogDebug("Deleted entity {Id} and {Count} related triples", id, toRemove.Count);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeEntity>> ListEntitiesAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (entities, _) = await EnsureIndexAsync(cancellationToken);
            return entities.Values.ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Triples ───────────────────────────────────────────────────────────────

    public async Task SaveTripleAsync(KnowledgeTriple triple, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (_, triples) = await EnsureIndexAsync(cancellationToken);

            var filePath = GetTripleFilePath(triple.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var json = JsonSerializer.Serialize(triple, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            triples[triple.Id] = triple;
            _logger.LogDebug("Saved triple {Id}: {Subject} --{Predicate}--> {Object}",
                triple.Id, triple.Subject, triple.Predicate, triple.Object);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeTriple>> GetTriplesForSubjectAsync(string subjectId, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (_, triples) = await EnsureIndexAsync(cancellationToken);
            return triples.Values
                .Where(t => t.Subject.Equals(subjectId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeTriple>> GetTriplesForObjectAsync(string objectId, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (_, triples) = await EnsureIndexAsync(cancellationToken);
            return triples.Values
                .Where(t => t.Object.Equals(objectId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeTriple>> TraverseAsync(
        IReadOnlyList<string> seedEntityIds,
        int maxHops = 2,
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (_, triples) = await EnsureIndexAsync(cancellationToken);
            return TraverseCore(triples, seedEntityIds, maxHops);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeleteTripleAsync(string id, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var (_, triples) = await EnsureIndexAsync(cancellationToken);

            if (!triples.Remove(id))
                return;

            var filePath = GetTripleFilePath(id);
            if (File.Exists(filePath))
                File.Delete(filePath);

            _logger.LogDebug("Deleted triple {Id}", id);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Graph traversal ──────────────────────────────────────────────────────

    /// <summary>
    /// BFS traversal up to <paramref name="maxHops"/> hops from <paramref name="seedEntityIds"/>.
    /// Returns all triples discovered during traversal, in discovery order.
    /// </summary>
    internal static IReadOnlyList<KnowledgeTriple> TraverseCore(
        Dictionary<string, KnowledgeTriple> allTriples,
        IReadOnlyList<string> seedEntityIds,
        int maxHops)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new HashSet<string>(seedEntityIds, StringComparer.OrdinalIgnoreCase);
        var result = new List<KnowledgeTriple>();
        var seenTriples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var hop = 0; hop < maxHops && frontier.Count > 0; hop++)
        {
            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var triple in allTriples.Values)
            {
                var subjectMatch = frontier.Contains(triple.Subject);
                var objectMatch = frontier.Contains(triple.Object);

                if (!subjectMatch && !objectMatch)
                    continue;

                if (!seenTriples.Add(triple.Id))
                    continue;

                result.Add(triple);

                // Add the "other side" of the triple to the next frontier
                if (subjectMatch && !visited.Contains(triple.Object))
                    nextFrontier.Add(triple.Object);
                if (objectMatch && !visited.Contains(triple.Subject))
                    nextFrontier.Add(triple.Subject);
            }

            visited.UnionWith(frontier);
            frontier = nextFrontier;
            frontier.ExceptWith(visited);
        }

        return result;
    }

    // ── Entity name matching ─────────────────────────────────────────────────

    /// <summary>
    /// Minimum character length for an entity name or alias to be matched against a query.
    /// Prevents short/generic names ("AI", "PR", "it") from matching on nearly every message.
    /// Mirrors <c>KeywordTierSelector.MinKeywordLength</c>.
    /// </summary>
    internal const int MinEntityNameLength = 3;

    /// <summary>
    /// Returns true when any of the entity's names (primary or aliases) appear in
    /// <paramref name="query"/> as a whole word/phrase. Names shorter than
    /// <see cref="MinEntityNameLength"/> characters are skipped to avoid noise.
    /// </summary>
    internal static bool MatchesName(KnowledgeEntity entity, string query)
    {
        var lowerQuery = query.ToLowerInvariant();

        if (entity.Name.Length >= MinEntityNameLength &&
            ContainsWholePhrase(lowerQuery, entity.Name.ToLowerInvariant()))
            return true;

        return entity.Aliases.Any(a =>
            a.Length >= MinEntityNameLength &&
            ContainsWholePhrase(lowerQuery, a.ToLowerInvariant()));
    }

    /// <summary>
    /// Returns true when <paramref name="keyword"/> appears in <paramref name="text"/>
    /// with word boundaries on each side where the keyword itself starts/ends with a
    /// word character. Prevents "to" matching inside "tomorrow" or entity names matching
    /// as substrings of unrelated words.
    /// Mirrors <c>KeywordTierSelector.ContainsWholePhrase</c>.
    /// </summary>
    internal static bool ContainsWholePhrase(string text, string keyword)
    {
        if (keyword.Length == 0) return false;

        var checkStart = char.IsLetterOrDigit(keyword[0]);
        var checkEnd = char.IsLetterOrDigit(keyword[^1]);
        var index = 0;

        while ((index = text.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
        {
            var startOk = !checkStart
                          || index == 0
                          || !char.IsLetterOrDigit(text[index - 1]);
            var end = index + keyword.Length;
            var endOk = !checkEnd
                        || end >= text.Length
                        || !char.IsLetterOrDigit(text[end]);

            if (startOk && endOk)
                return true;

            index++;
        }

        return false;
    }

    // ── File paths ───────────────────────────────────────────────────────────

    private string GetEntityFilePath(string id) =>
        Path.Combine(_basePath, "entities", $"{id}.json");

    private string GetTripleFilePath(string id) =>
        Path.Combine(_basePath, "triples", $"{id}.json");

    // ── Index loading ────────────────────────────────────────────────────────

    private async Task<(Dictionary<string, KnowledgeEntity> Entities, Dictionary<string, KnowledgeTriple> Triples)> EnsureIndexAsync(
        CancellationToken cancellationToken)
    {
        if (_entities is not null && _triples is not null)
            return (_entities, _triples);

        _entities = new Dictionary<string, KnowledgeEntity>(StringComparer.OrdinalIgnoreCase);
        _triples = new Dictionary<string, KnowledgeTriple>(StringComparer.OrdinalIgnoreCase);

        var entitiesDir = Path.Combine(_basePath, "entities");
        var triplesDir = Path.Combine(_basePath, "triples");

        if (Directory.Exists(entitiesDir))
        {
            foreach (var file in Directory.EnumerateFiles(entitiesDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var entity = JsonSerializer.Deserialize<KnowledgeEntity>(json, JsonOptions);
                    if (entity is not null)
                        _entities[entity.Id] = entity;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Skipping malformed entity file: {Path}", file);
                }
            }
        }

        if (Directory.Exists(triplesDir))
        {
            foreach (var file in Directory.EnumerateFiles(triplesDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var triple = JsonSerializer.Deserialize<KnowledgeTriple>(json, JsonOptions);
                    if (triple is not null)
                        _triples[triple.Id] = triple;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Skipping malformed triple file: {Path}", file);
                }
            }
        }

        _logger.LogDebug("Loaded knowledge graph: {Entities} entities, {Triples} triples from {Path}",
            _entities.Count, _triples.Count, _basePath);

        return (_entities, _triples);
    }
}
