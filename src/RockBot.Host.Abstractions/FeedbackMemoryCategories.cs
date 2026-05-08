namespace RockBot.Host;

/// <summary>
/// Well-known long-term memory category names for feedback-shaped entries (rules,
/// directives, user reversals). Phase 3 self-repair contradiction detection scopes
/// to entries whose category sits under <see cref="Prefix"/>.
/// </summary>
/// <remarks>
/// User-tagged corrections (entries under <see cref="UserCorrectionPrefix"/> or carrying
/// the <see cref="UserCorrectionTag"/> tag) are treated as authoritative: they always win
/// over agent-self entries when a contradiction is detected, regardless of recency.
/// </remarks>
public static class FeedbackMemoryCategories
{
    /// <summary>Category prefix for all feedback entries.</summary>
    public const string Prefix = "feedback";

    /// <summary>Category prefix for user-issued corrections (always-wins).</summary>
    public const string UserCorrectionPrefix = "feedback/from-user";

    /// <summary>Tag value that marks an entry as a user correction (always-wins).</summary>
    public const string UserCorrectionTag = "correction";

    /// <summary>
    /// Returns <c>true</c> when the given category names a feedback memory.
    /// Accepts <c>null</c> and returns <c>false</c>.
    /// </summary>
    public static bool IsFeedbackMemory(string? category) =>
        category is not null
        && (category.Equals(Prefix, StringComparison.Ordinal)
            || category.StartsWith(Prefix + "/", StringComparison.Ordinal));

    /// <summary>
    /// Returns <c>true</c> when the entry should be treated as a user correction —
    /// either by category prefix or by the well-known tag.
    /// </summary>
    public static bool IsUserCorrection(MemoryEntry entry)
    {
        if (entry.Category is not null
            && (entry.Category.Equals(UserCorrectionPrefix, StringComparison.Ordinal)
                || entry.Category.StartsWith(UserCorrectionPrefix + "/", StringComparison.Ordinal)))
        {
            return true;
        }

        foreach (var tag in entry.Tags)
        {
            if (string.Equals(tag, UserCorrectionTag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
