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

    /// <summary>The refusal every writer returns for a code-owned skill.</summary>
    public static string RefusalMessage(string name) =>
        $"'{name}' is a built-in tool guide maintained in code and can't be changed. " +
        $"Save your own lessons as a separate skill (for example '{CompanionName(name)}') that refers to it.";
}
