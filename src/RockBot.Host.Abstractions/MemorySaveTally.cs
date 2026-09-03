namespace RockBot.Host;

/// <summary>
/// Counts what a batch of deduplicated saves actually did, so a pass can report "3 saved,
/// 5 reinforced" instead of "8 entries saved" when five of them created nothing.
/// </summary>
public sealed class MemorySaveTally
{
    public int Saved { get; private set; }
    public int Reinforced { get; private set; }
    public int Extended { get; private set; }

    public void Record(MemorySaveOutcome outcome)
    {
        switch (outcome.Action)
        {
            case MemorySaveAction.Reinforced:
                Reinforced++;
                break;
            case MemorySaveAction.Extended:
                Extended++;
                break;
            default:
                Saved++;
                break;
        }
    }
}
