namespace RockBot.Host;

/// <summary>
/// Where a long-term memory came from, stored under the <see cref="MetadataKey"/> metadata key:
/// <c>user</c> (the user said or confirmed it), <c>tool:&lt;name&gt;</c> (read from a tool
/// result) or <c>inferred</c> (the agent's own conclusion — snippets, patterns, guesses).
/// Issue #667: a "verified" fact built from search snippets looked the same as one read from
/// the source, so nothing downstream could weigh or re-check it.
/// </summary>
public static class MemoryProvenance
{
    /// <summary>Metadata key holding the source. The dream's preference pass already writes it.</summary>
    public const string MetadataKey = "source";

    public const string User = "user";
    public const string Inferred = "inferred";
    public const string ToolPrefix = "tool:";

    /// <summary>
    /// Canonical form of a caller-supplied source, or null when it is not one of the three
    /// shapes. "inference" is accepted for <see cref="Inferred"/>.
    /// </summary>
    public static string? Normalize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var trimmed = source.Trim();
        if (trimmed.Equals(User, StringComparison.OrdinalIgnoreCase))
            return User;
        if (trimmed.Equals(Inferred, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("inference", StringComparison.OrdinalIgnoreCase))
            return Inferred;

        if (trimmed.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[ToolPrefix.Length..].Trim();
            if (name.Length is > 0 and <= 64 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/'))
                return ToolPrefix + name;
        }

        return null;
    }

    /// <summary>The entry's source, or null when it has none (entries saved before #667).</summary>
    public static string? Get(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(MetadataKey, out var value) ? Normalize(value) : null;

    /// <summary>
    /// <paramref name="metadata"/> with the source set, or removed when <paramref name="source"/>
    /// is null. Returns null for an empty result so entries without metadata stay that way.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? With(IReadOnlyDictionary<string, string>? metadata, string? source)
    {
        var result = metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

        if (source is null)
            result.Remove(MetadataKey);
        else
            result[MetadataKey] = source;

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// The source for one fact observed again. More evidence can only raise trust: an
    /// inferred fact the user then states becomes <see cref="User"/>.
    /// </summary>
    public static string? MostTrusted(string? a, string? b) => Rank(b) > Rank(a) ? b : a;

    /// <summary>
    /// The source for text combined from several entries: no more trusted than its weakest
    /// part, so merging cannot launder an inferred fact into a trusted one. Any inferred part
    /// makes the whole inferred; otherwise any part without a source makes the source unknown.
    /// </summary>
    public static string? LeastTrusted(IEnumerable<string?> sources)
    {
        var list = sources.ToList();
        if (list.Count == 0)
            return null;
        if (list.Contains(Inferred))
            return Inferred;
        if (list.Any(s => s is null))
            return null;
        return list.MinBy(Rank);
    }

    /// <summary><c> source=…</c> for prompt and recall lines, or empty when there is none.</summary>
    public static string Format(IReadOnlyDictionary<string, string>? metadata) =>
        Get(metadata) is { } source ? $" source={source}" : string.Empty;

    private static int Rank(string? source) => source switch
    {
        null => 0,
        Inferred => 1,
        User => 3,
        _ => 2 // tool:<name>
    };
}
