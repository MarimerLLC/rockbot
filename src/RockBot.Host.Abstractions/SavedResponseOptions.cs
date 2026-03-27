namespace RockBot.Host;

/// <summary>
/// Configuration for the saved-response store.
/// </summary>
public sealed class SavedResponseOptions
{
    public string BasePath { get; set; } = "saved-responses";
}
