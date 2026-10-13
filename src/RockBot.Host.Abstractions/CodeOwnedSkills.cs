namespace RockBot.Host;

/// <summary>
/// Rules shared by every skill writer for code-owned skills (#699): the stored copies of each
/// tool guide, which <c>StarterSkillService</c> seeds and re-syncs from code at startup.
/// The agent's skill tools, the dream passes and repair tickets refuse to change their content,
/// summary, name or existence. Lessons about a guide go to a companion skill instead
/// (<see cref="CompanionName"/>) that refers back to it, and every path that delivers a guide
/// appends the companion's lessons (<see cref="LessonsBlock"/>).
/// </summary>
public static class CodeOwnedSkills
{
    /// <summary>Suffix of the companion skill that holds lessons learned about a guide.</summary>
    public const string CompanionSuffix = "-learned";

    /// <summary>
    /// Longest companion text appended to a guide in full. A longer companion is reduced to its
    /// summary and a pointer to <c>get_skill</c>.
    /// </summary>
    public const int LessonsInlineLimit = 4000;

    /// <summary>True when <paramref name="skill"/> is a code-owned starter skill.</summary>
    public static bool IsCodeOwned(Skill? skill) => skill?.CodeOwned == true;

    /// <summary>
    /// The protected skill names: every registered tool guide, plus any stored skill flagged
    /// <see cref="Skill.CodeOwned"/>. Case-insensitive. Guide names count even when the stored
    /// copy is missing, so a writer can't take the name before the next startup re-seeds it.
    /// </summary>
    public static HashSet<string> Names(IEnumerable<string> guideNames, IEnumerable<Skill> skills)
    {
        var names = new HashSet<string>(guideNames, StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            if (skill.CodeOwned)
                names.Add(skill.Name);
        }
        return names;
    }

    /// <summary>The companion skill for lessons learned using guide <paramref name="name"/>.</summary>
    public static string CompanionName(string name) => name + CompanionSuffix;

    /// <summary>
    /// The companion of guide <paramref name="guideName"/>, ready to append a lesson to:
    /// <paramref name="existing"/> with a <see cref="Skill.SeeAlso"/> link back to the guide
    /// added if it lacks one, or a new (unsaved) stub when there is no companion yet.
    /// </summary>
    public static Skill Companion(Skill? existing, string guideName, DateTimeOffset now)
    {
        if (existing is not null)
        {
            var seeAlso = existing.SeeAlso ?? [];
            return seeAlso.Contains(guideName, StringComparer.OrdinalIgnoreCase)
                ? existing
                : existing with { SeeAlso = [.. seeAlso, guideName] };
        }

        return new Skill(
            Name: CompanionName(guideName),
            Summary: $"Lessons learned using the {guideName} tool guide.",
            Content: CompanionPreamble(guideName),
            CreatedAt: now,
            UpdatedAt: now,
            SeeAlso: [guideName]);
    }

    /// <summary>The opening of a new companion: its heading and a pointer to the guide.</summary>
    private static string CompanionPreamble(string guideName) =>
        $"# {guideName}: lessons learned\n\n" +
        $"Read the built-in guide first with `get_tool_guide(\"{guideName}\")`; " +
        "these are lessons learned while following it.";

    /// <summary>
    /// The block appended to guide <paramref name="guideName"/> wherever it's delivered
    /// (<c>get_tool_guide</c>, <c>get_skill</c>, auto-recall): the companion's lessons, or its
    /// summary and a <c>get_skill</c> pointer when the text exceeds <see cref="LessonsInlineLimit"/>.
    /// The companion's own preamble is dropped, since the guide is right above it.
    /// </summary>
    public static string LessonsBlock(Skill companion, string guideName)
    {
        var lessons = companion.Content.Trim();
        var preamble = CompanionPreamble(guideName);
        if (lessons.StartsWith(preamble, StringComparison.Ordinal))
            lessons = lessons[preamble.Length..].Trim();

        var header = $"---\n## Lessons learned (skill: {companion.Name})\n\n";
        if (lessons.Length <= LessonsInlineLimit)
            return header + lessons;

        var summary = string.IsNullOrWhiteSpace(companion.Summary)
            ? $"Lessons learned using the {guideName} tool guide."
            : companion.Summary.Trim();
        return header + summary + "\n\n" +
               $"These lessons are too long to include here; load them with `get_skill(\"{companion.Name}\")`.";
    }

    /// <summary>
    /// <see cref="LessonsBlock"/> for guide <paramref name="guideName"/>, or <c>null</c> when it
    /// has no companion or the companion holds no lessons yet. With <paramref name="markUsed"/>,
    /// an explicit load of the guide counts as using the companion, so the dream prune keeps it.
    /// </summary>
    public static async Task<string?> LessonsBlockAsync(ISkillStore store, string guideName, bool markUsed)
    {
        var companion = await store.GetAsync(CompanionName(guideName));
        if (companion is null || !HasLessons(companion, guideName))
            return null;

        if (markUsed)
            await store.SaveAsync(companion with { LastUsedAt = DateTimeOffset.UtcNow });

        return LessonsBlock(companion, guideName);
    }

    /// <summary>True when the companion holds more than its stub preamble.</summary>
    public static bool HasLessons(Skill companion, string guideName) =>
        companion.Content.Trim() != CompanionPreamble(guideName);

    /// <summary>The refusal every writer returns for a code-owned skill.</summary>
    public static string RefusalMessage(string name) =>
        $"'{name}' is a built-in tool guide maintained in code and can't be changed. " +
        $"Save your own lessons as a separate skill (for example '{CompanionName(name)}') that refers to it.";
}
