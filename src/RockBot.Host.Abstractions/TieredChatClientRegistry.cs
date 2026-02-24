using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Holds one <see cref="IChatClient"/> per <see cref="ModelTier"/>.
/// Registered as a singleton; <see cref="LlmClient"/> (transient) resolves
/// the appropriate client for each call via <see cref="GetClient"/>.
/// </summary>
public sealed class TieredChatClientRegistry(
    IChatClient low, IChatClient balanced, IChatClient high)
{
    /// <summary>Returns the chat client for the requested tier.</summary>
    public IChatClient GetClient(ModelTier tier) => tier switch
    {
        ModelTier.Low  => low,
        ModelTier.High => high,
        _              => balanced
    };

    /// <summary>
    /// Returns the model ID reported by the client's metadata for the given tier,
    /// or <c>null</c> when the metadata is unavailable.
    /// </summary>
    public string? GetModelId(ModelTier tier) =>
        GetClient(tier).GetService<ChatClientMetadata>()?.DefaultModelId;
}
