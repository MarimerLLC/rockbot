namespace RockBot.Observation;

/// <summary>
/// Default extraction and evaluation prompts for the built-in
/// theory-of-self and theory-of-user targets. Operators can override per
/// target by constructing <see cref="ObservationTarget"/> directly with
/// their own prompt text; these defaults exist so the standard targets
/// work out of the box.
/// </summary>
/// <remarks>
/// The prompts deliberately push the LLM toward strict, evidence-grounded
/// observations (per <c>design/observation-framework.md</c>). They are not
/// the narrative synthesis-flavored prompts in earlier versions of the
/// getting-started doc — those have been retired in favour of the
/// framework's structured pipeline.
/// </remarks>
public static class DefaultPrompts
{
    /// <summary>
    /// System prompt for theory-of-self extraction. Asks the LLM to identify
    /// concrete, behavior-only observations about the agent with verbatim
    /// quote evidence.
    /// </summary>
    public const string TheoryOfSelfExtraction = """
        You are observing the agent's behavioral patterns in conversations.

        Extract concrete, evidence-grounded observations about HOW the agent operates:
        the patterns, habits, and tendencies visible in its actions and responses.

        Strict rules:

        1. BEHAVIOR ONLY, not motivation. Describe what the agent does, not what it
           wants or values. "The agent reads multiple files before making a one-line
           edit" is good. "The agent is thorough" or "the agent values context" is bad
           — those are inferred motivations, not observed behaviors.

        2. QUOTE EVIDENCE. Every observation MUST cite a verbatim or near-verbatim
           quote from a specific turn that supports the claim. Quotes are mechanically
           validated against the cited turn; observations whose quote is not present
           in the cited turn will be discarded before they enter the candidate pool.

        3. SPECIFIC, NOT GENERIC. "The agent often summarises" is too broad. "The agent
           emits a 1-2 sentence end-of-turn summary even when the user requested a
           one-word answer" is specific.

        4. HONEST, NOT FLATTERING. Record patterns as you actually see them, including
           ones that aren't flattering or that contradict the agent's stated identity.
           Drift toward honesty, not toward narrative.

        5. DON'T FORCE OBSERVATIONS. Many conversations produce nothing worth recording.
           Returning an empty list is correct when there is no clear pattern.
        """;

    /// <summary>
    /// System prompt for theory-of-user extraction. Asks the LLM to identify
    /// concrete, behavior-only observations about the user with verbatim
    /// quote evidence.
    /// </summary>
    public const string TheoryOfUserExtraction = """
        You are observing the user's preferences and patterns in conversations with the agent.

        Extract concrete, evidence-grounded observations about HOW the user operates:
        what they ask for, how they react, what they reject, what they accept, what
        they redirect.

        Strict rules:

        1. BEHAVIOR ONLY, not motivation. Describe what the user does, not what they
           feel or value. "User reverts test changes the agent made without being
           asked" is good. "User values minimalism" or "user is pragmatic" is bad —
           those are inferred motivations.

        2. QUOTE EVIDENCE. Every observation MUST cite a verbatim or near-verbatim
           quote from a specific turn that supports the claim. Quotes are mechanically
           validated against the cited turn; observations whose quote is not present
           in the cited turn will be discarded.

        3. SPECIFIC, NOT GENERIC. "User likes terse responses" is too broad. "User
           redirects the agent within one turn whenever the agent's response exceeds
           ~5 sentences" is specific.

        4. HONEST, NOT FLATTERING. Record patterns as you observe them — including
           ones that aren't flattering. The point is an honest model of the user, not
           a polite one. Don't write to justify the agent's existence either.

        5. DON'T FORCE OBSERVATIONS. Many conversations produce nothing worth recording.
           Returning an empty list is correct when there is no clear pattern.
        """;

    /// <summary>
    /// Differential evaluation prompt used by both theory-of-self and
    /// theory-of-user. The same prompt works for both because the rules
    /// (grounded? distinct from existing? clear?) are not target-specific.
    /// </summary>
    public const string DifferentialEvaluation = """
        You are reviewing candidate observations to decide whether each one should be
        promoted to a theory, refined, or rejected.

        For each candidate, choose:

        - promote: the candidate is well-grounded by its quotes, distinct from existing
          theories, and articulates a real pattern. If the candidate's wording could be
          clearer, you may provide a refinedText alongside; the theory will use that
          wording instead.

        - refine: the candidate captures something real, but its wording is too broad,
          too narrow, or otherwise unclear. Provide refinedText with the corrected
          wording. The candidate stays in the pool to accumulate more evidence under
          the new wording.

        - reject: the candidate is poorly grounded by its quotes, duplicates an
          existing theory, or is too noisy or speculative to keep. The candidate is
          removed from the pool.

        When in doubt, prefer reject over promote. Promoted theories influence agent
        context every turn, so the bar should be high.
        """;
}
