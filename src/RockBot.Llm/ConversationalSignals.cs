using System.Text.RegularExpressions;

namespace RockBot.Llm;

/// <summary>
/// Message-shape heuristics used by <see cref="KeywordTierSelector"/>'s thread-aware rules
/// (#663). Kept separate from the scoring keyword lists: these are compiled, not dream-tuned,
/// because they decide when the thread-state floors may be skipped or applied.
/// </summary>
public static class ConversationalSignals
{
    /// <summary>
    /// Longest message, in characters, that can count as a trivial acknowledgement. Anything
    /// longer carries content worth a capable model even if it opens with "thanks".
    /// </summary>
    public const int TrivialAckMaxChars = 60;

    // Whole phrases that are pure acknowledgement or greeting. Matched against the message's
    // word sequence, longest first. Approvals that tell the agent to *continue* work — "yes",
    // "sure", "go ahead", "do it", "please" — are deliberately absent: they are instructions
    // on a thread, not acknowledgements. "ok"/"okay" are included per the #663 design; if an
    // "ok" turns into side-effecting work, mid-turn escalation (TierEscalationContext) lifts it.
    private static readonly string[][] AckPhrases = BuildPhrases(
    [
        "thanks", "thank you", "thank you very much", "thanks a lot", "thanks so much", "thx", "ty", "tysm",
        "many thanks", "much appreciated", "appreciate it", "appreciated", "cheers",
        "ok", "okay", "k", "kk", "okey dokey", "alright", "all right",
        "got it", "gotcha", "understood", "noted", "makes sense", "fair enough",
        "cool", "great", "nice", "perfect", "awesome", "excellent", "wonderful", "brilliant",
        "good", "very good", "sounds good", "sounds great", "looks good", "looks great",
        "that's great", "thats great", "that works", "all good", "no worries", "no problem", "np",
        "love it", "nice work", "good job", "great job", "well done",
        "hi", "hello", "hey", "hiya", "howdy", "yo",
        "good morning", "good afternoon", "good evening", "good night", "morning", "night",
        "bye", "goodbye", "see you", "see ya", "later", "talk later", "ttyl",
        "lol", "haha", "hah", "wow", "oh", "ah",
    ]);

    // Words that may sit between ack phrases without adding content ("ok, thanks again",
    // "thanks for that", "great, thanks so much"). An unknown word disqualifies the message.
    private static readonly HashSet<string> AckFillers = new(StringComparer.Ordinal)
    {
        "so", "much", "very", "really", "a", "lot", "again", "for", "that", "this", "the",
        "help", "all", "and", "just", "well", "you", "man", "buddy", "mate", "rockbot",
    };

    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="text"/> is a pure acknowledgement or greeting — "thanks",
    /// "ok", "got it", "👍", "ok thanks!" — short, and made only of conversational ack
    /// phrases (plus a few filler words, punctuation and emoji). Such a turn may route Low
    /// even on an established thread. Anything carrying an instruction or a question
    /// ("ok, create a deck", "thanks — now fix the outline") is not trivial.
    /// </summary>
    public static bool IsTrivialAck(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length > TrivialAckMaxChars)
            return false;

        // A question is never a pure acknowledgement ("ok?" asks for something).
        if (trimmed.Contains('?'))
            return false;

        var words = WordRegex.Matches(trimmed.ToLowerInvariant().Replace('’', '\''))
            .Select(m => m.Value)
            .ToArray();

        // Emoji/punctuation only ("👍", "🙏", "!!") — a reaction, not a request.
        if (words.Length == 0)
            return true;

        var matchedPhrase = false;
        var i = 0;
        while (i < words.Length)
        {
            var phraseLength = MatchPhraseAt(words, i);
            if (phraseLength > 0)
            {
                matchedPhrase = true;
                i += phraseLength;
                continue;
            }

            if (AckFillers.Contains(words[i]))
            {
                i++;
                continue;
            }

            return false;
        }

        return matchedPhrase;
    }

    private static int MatchPhraseAt(string[] words, int start)
    {
        foreach (var phrase in AckPhrases) // longest first
        {
            if (start + phrase.Length > words.Length)
                continue;

            var match = true;
            for (var j = 0; j < phrase.Length; j++)
            {
                if (!string.Equals(words[start + j], phrase[j], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return phrase.Length;
        }

        return 0;
    }

    private static string[][] BuildPhrases(string[] phrases) =>
        phrases
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .OrderByDescending(p => p.Length)
            .ToArray();

    // ── Research-shaped questions ─────────────────────────────────────────────

    /// <summary>Minimum word count for a question to count as research-shaped.</summary>
    public const int ResearchQuestionMinWords = 6;

    private static readonly Regex QuestionOpenerRegex = new(
        @"^\s*(what|which|why|how|who|when|where|does|do|did|is|are|can|could|should|would|will|explain|describe|summari[sz]e|tell me|find out|look into)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // An all-caps token of two or more characters (MCP, REST, CSLA, OAuth2 is not caught —
    // fine) — the common shape of a named technical subject in a question.
    private static readonly Regex AcronymRegex = new(@"\b[A-Z][A-Z0-9]{1,9}\b", RegexOptions.Compiled);

    // Acronyms that name nothing technical.
    private static readonly HashSet<string> NonTechnicalAcronyms = new(StringComparer.Ordinal)
    {
        "OK", "AM", "PM", "TV", "ASAP", "FYI", "BTW", "LOL", "TBD", "ETA", "IMO", "IMHO", "OMG", "US", "UK", "USA", "EU",
    };

    // Versions ("version 2", "v2.1", "2.0") and technical-artifact words.
    private static readonly Regex TechnicalMarkerRegex = new(
        @"\bversion\s+\d|\bv\d+(\.\d+)*\b|\b\d+\.\d+(\.\d+)*\b" +
        @"|\b(spec|specs|specification|specifications|protocol|rfc|api|apis|sdk|sdks|changelog|release notes)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SentenceSplitRegex = new(@"[.!?]+\s+", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="text"/> is a research-shaped question about a named technical
    /// subject — "what are the key features of the MCP version 2 spec" — rather than trivia
    /// ("what is the capital of France?"). The small Low-tier model answers these from prior
    /// knowledge instead of searching, so they are floored at Balanced. Requires a question
    /// form, at least <see cref="ResearchQuestionMinWords"/> words, and a technical marker:
    /// a technical acronym, a version, or a spec/protocol/API word. See #663.
    /// </summary>
    public static bool IsResearchQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        var wordCount = trimmed.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < ResearchQuestionMinWords)
            return false;

        // Question form: any sentence opens with a question word, or the message asks one.
        var isQuestion = trimmed.Contains('?')
            || SentenceSplitRegex.Split(trimmed).Any(s => QuestionOpenerRegex.IsMatch(s));
        if (!isQuestion)
            return false;

        if (TechnicalMarkerRegex.IsMatch(trimmed))
            return true;

        return AcronymRegex.Matches(trimmed)
            .Any(m => !NonTechnicalAcronyms.Contains(m.Value));
    }
}
