namespace RockBot.Llm.Copilot;

/// <summary>
/// Configuration options for a single Copilot chat client instance.
/// </summary>
public sealed class CopilotChatClientOptions
{
    /// <summary>Copilot model to use (e.g. "gpt-4.1", "claude-sonnet-4").</summary>
    public string ModelId { get; set; } = "gpt-4.1";

    /// <summary>When true, authenticates via the logged-in GitHub CLI user.</summary>
    public bool UseLoggedInUser { get; set; } = true;

    /// <summary>Explicit GitHub token override. Takes precedence over CLI auth when set.</summary>
    public string? GitHubToken { get; set; }

    /// <summary>Timeout for a single Copilot request (session create + send + response).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Maximum retries on rate-limit errors before propagating.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay for exponential backoff on rate-limit retries.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
}
