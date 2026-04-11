namespace RockBot.A2A;

/// <summary>
/// Detects when the same InputRequired question/answer pair keeps repeating,
/// indicating a stuck conversation loop. Modeled after
/// <see cref="Host.AgentLoopRunner.RepetitiveToolCallDetector"/>.
/// </summary>
internal sealed class InputRequiredRepetitionDetector(int threshold = 3)
{
    private string? _lastKey;
    private int _count;

    /// <summary>
    /// Records a question/answer pair. Returns <c>true</c> when the threshold of
    /// consecutive identical pairs is reached (and resets the internal state so
    /// the next call starts a fresh run).
    /// </summary>
    public bool Track(string question, string answer)
    {
        var questionTrunc = question is { Length: > 500 } ? question[..500] : question;
        var answerTrunc = answer is { Length: > 500 } ? answer[..500] : answer;
        var key = $"{questionTrunc}|{answerTrunc}";

        if (key == _lastKey)
        {
            _count++;
        }
        else
        {
            _lastKey = key;
            _count = 1;
        }

        if (_count >= threshold)
        {
            Reset();
            return true;
        }

        return false;
    }

    /// <summary>Resets tracking state.</summary>
    public void Reset()
    {
        _lastKey = null;
        _count = 0;
    }
}
