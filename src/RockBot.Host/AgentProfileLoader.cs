using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Hosted service that loads the <see cref="AgentProfile"/> during startup
/// and registers it as a singleton for injection into handlers.
/// </summary>
internal sealed class AgentProfileLoader(
    IAgentProfileProvider provider,
    ProfileHolder holder,
    ILogger<AgentProfileLoader> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading agent profile...");
        var profile = await provider.LoadAsync(cancellationToken);
        holder.Profile = profile;
        logger.LogInformation("Agent profile loaded successfully");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Holds the loaded <see cref="AgentProfile"/> singleton.
/// Registered as a singleton so the profile can be set during startup
/// and resolved by handlers at any time.
/// </summary>
internal sealed class ProfileHolder
{
    private AgentProfile? _profile;

    public AgentProfile Profile
    {
        get => _profile ?? throw new InvalidOperationException(
            "Agent profile has not been loaded yet. Ensure AgentProfileLoader has started.");
        set => _profile = value;
    }
}
