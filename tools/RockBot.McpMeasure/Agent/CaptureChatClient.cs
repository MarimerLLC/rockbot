using Microsoft.Extensions.AI;
using RockBot.Host;

namespace RockBot.McpMeasure.Agent;

/// <summary>One LLM round trip as the provider saw it.</summary>
public sealed record LlmRequestRecord(
    int ToolsOffered,
    IReadOnlyList<string> ToolNames,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    IReadOnlyList<FunctionCallContent> Calls,
    string Text);

/// <summary>
/// Sits directly over the provider client, under the function-invoking loop, so it sees every
/// LLM round trip of a turn: the tools offered, the token usage, and the calls the model made.
/// Runs are sequential, so it records into whichever turn is current.
/// </summary>
public sealed class CaptureChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private List<LlmRequestRecord> _current = [];

    /// <summary>Starts recording a new turn and returns its list.</summary>
    public List<LlmRequestRecord> BeginTurn() => _current = [];

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        var names = options?.Tools?.Select(t => t.Name).ToList() ?? [];
        _current.Add(new LlmRequestRecord(
            names.Count,
            names,
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.CachedInputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0,
            [.. response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()],
            response.Text));
        return response;
    }
}

/// <summary>The agent loop's view of the model: every tier is the one model under test.</summary>
internal sealed class SingleModelLlmClient(IChatClient client) : ILlmClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
        client.GetResponseAsync(messages, options, ct);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken ct) =>
        client.GetResponseAsync(messages, options, ct);
}
