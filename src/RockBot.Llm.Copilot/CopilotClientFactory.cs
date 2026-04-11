using GitHub.Copilot.SDK;

namespace RockBot.Llm.Copilot;

/// <summary>
/// Factory for creating and starting a <see cref="CopilotClient"/> singleton.
/// </summary>
public static class CopilotClientFactory
{
    /// <summary>
    /// Creates a new <see cref="CopilotClient"/>, starts it, and returns the ready-to-use instance.
    /// </summary>
    public static async Task<CopilotClient> CreateAndStartAsync(
        CopilotChatClientOptions options,
        CancellationToken cancellationToken = default)
    {
        var clientOptions = new CopilotClientOptions();

        if (!string.IsNullOrEmpty(options.GitHubToken))
        {
            clientOptions.Environment = new Dictionary<string, string>
            {
                ["GITHUB_TOKEN"] = options.GitHubToken
            };
        }

        var client = new CopilotClient(clientOptions);
        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }
}
