using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Council;

namespace RockBot.AdvisorCouncil.Personas;

/// <summary>
/// Loads council personas from markdown files with YAML frontmatter. Supports manual
/// <see cref="Reload"/> calls — wiring to a file watcher is done by
/// <see cref="PersonaRegistryHotReload"/>.
/// </summary>
internal sealed class PersonaRegistry
{
    private readonly ILogger<PersonaRegistry> _logger;
    private readonly string _personasPath;
    private volatile PersonaSnapshot _snapshot;

    public PersonaRegistry(IOptions<CouncilOptions> options, ILogger<PersonaRegistry> logger)
    {
        _logger = logger;
        _personasPath = ResolvePersonasPath(options.Value.PersonasPath);
        _snapshot = Load();
    }

    public IReadOnlyDictionary<string, Persona> Personas => _snapshot.Personas;

    public string PersonaSetHash => _snapshot.Hash;

    public string PersonasPath => _personasPath;

    public void Reload()
    {
        var next = Load();
        if (next.Hash == _snapshot.Hash)
        {
            _logger.LogDebug("Persona reload: no changes (hash {Hash})", next.Hash[..8]);
            return;
        }

        _logger.LogInformation(
            "Persona reload: {Count} personas, hash {OldHash} -> {NewHash}",
            next.Personas.Count, _snapshot.Hash[..8], next.Hash[..8]);
        _snapshot = next;
    }

    private static string ResolvePersonasPath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;

        var pvcPath = "/data/advisor-council/personas";
        if (Directory.Exists(pvcPath))
            return pvcPath;

        var local = Path.Combine(AppContext.BaseDirectory, "agent", "personas");
        return local;
    }

    private PersonaSnapshot Load()
    {
        var personas = new ConcurrentDictionary<string, Persona>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_personasPath))
        {
            _logger.LogWarning("Personas path does not exist: {Path}", _personasPath);
            return new PersonaSnapshot(personas, ComputeHash(personas));
        }

        foreach (var file in Directory.GetFiles(_personasPath, "*.md"))
        {
            try
            {
                var persona = ParsePersonaFile(file);
                if (persona is not null)
                    personas[persona.Id] = persona;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse persona file {File}", file);
            }
        }

        if (personas.IsEmpty)
            _logger.LogWarning("No personas loaded from {Path}", _personasPath);

        return new PersonaSnapshot(personas, ComputeHash(personas));
    }

    internal static Persona? ParsePersonaFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        return ParsePersonaContent(content, Path.GetFileNameWithoutExtension(filePath));
    }

    /// <summary>
    /// Parses a persona file body. Frontmatter is delimited by lines containing only "---".
    /// Frontmatter keys: id, name, description, default_research. Body is the system prompt.
    /// </summary>
    internal static Persona? ParsePersonaContent(string content, string filename)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---")
            return null;

        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int endFm = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                endFm = i;
                break;
            }
            var line = lines[i];
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;
            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();
            if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                value = value[1..^1];
            frontmatter[key] = value;
        }
        if (endFm < 0)
            return null;

        var bodyLines = lines.Skip(endFm + 1);
        var body = string.Join('\n', bodyLines).Trim();
        if (body.Length == 0)
            return null;

        var id = frontmatter.GetValueOrDefault("id", filename).Trim();
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var name = frontmatter.GetValueOrDefault("name", id);
        var description = frontmatter.GetValueOrDefault("description", string.Empty);
        var defaultResearchStr = frontmatter.GetValueOrDefault("default_research", "false");
        var defaultResearch = bool.TryParse(defaultResearchStr, out var b) && b;

        return new Persona(id, name, description, body, defaultResearch);
    }

    /// <summary>
    /// SHA-256 over <c>id|systemPrompt</c> pairs sorted by id, for surfacing in output
    /// metadata so callers can detect persona drift across deployments.
    /// </summary>
    internal static string ComputeHash(IReadOnlyDictionary<string, Persona> personas)
    {
        var sb = new StringBuilder();
        foreach (var p in personas.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(p.Value.Id).Append('|').Append(p.Value.SystemPrompt).Append('\n');
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record PersonaSnapshot(IReadOnlyDictionary<string, Persona> Personas, string Hash);
}
