namespace RockBot.Host;

/// <summary>
/// Rules shared by every skill writer for code-owned skills (#699): the stored copies of each
/// tool guide, which <c>StarterSkillService</c> seeds and re-syncs from code at startup.
/// The agent's skill tools, the dream passes and repair tickets refuse to change their content,
/// summary, name or existence. Lessons about a guide go to a companion skill instead
/// (<see cref="CompanionName"/>) that refers back to it.
/// </summary>
public static class CodeOwnedSkills
{
    /// <summary>Suffix of the companion skill that holds lessons learned about a guide.</summary>
    public const string CompanionSuffix = "-learned";

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
            Content: $"# {guideName}: lessons learned\n\n" +
                     $"Read the built-in guide first with `get_tool_guide(\"{guideName}\")`; " +
                     "these are lessons learned while following it.",
            CreatedAt: now,
            UpdatedAt: now,
            SeeAlso: [guideName]);
    }

    /// <summary>The refusal every writer returns for a code-owned skill.</summary>
    public static string RefusalMessage(string name) =>
        $"'{name}' is a built-in tool guide maintained in code and can't be changed. " +
        $"Save your own lessons as a separate skill (for example '{CompanionName(name)}') that refers to it.";
}
