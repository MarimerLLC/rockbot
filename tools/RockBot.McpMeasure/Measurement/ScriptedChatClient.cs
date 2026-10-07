using Microsoft.Extensions.AI;
using RockBot.Tools.Mcp;

namespace RockBot.McpMeasure.Measurement;

/// <summary>
/// A model that does the right thing on whatever surface it is offered, so the whole pipeline —
/// loop, gateway, bridge, fixtures, grader — can be run end to end without an API key: the typed
/// tool when it is in the list; otherwise <c>mcp_find_tools</c> once, when that is offered; otherwise
/// <c>mcp_invoke_tool</c>. Once the call has a result it answers. It finds the turn it is on by the
/// user message, against <see cref="MeasureTasks.All"/>.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    public const string ModelId = "scripted";

    private readonly Dictionary<string, MeasureTurn> _turns = MeasureTasks.All
        .SelectMany(t => t.Turns)
        .ToDictionary(t => t.Prompt, StringComparer.Ordinal);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var lastUser = list.FindLastIndex(m => m.Role == ChatRole.User && _turns.ContainsKey(m.Text));
        if (lastUser < 0)
            return Reply(new ChatMessage(ChatRole.Assistant, "I don't know this task."));

        var turn = _turns[list[lastUser].Text];
        var sinceUser = list.Skip(lastUser + 1).ToList();
        var calls = sinceUser.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        var typed = McpWrapperNaming.For(turn.Server, turn.Tool);

        if (calls.Any(c => c.Name == typed || c.Name == "mcp_invoke_tool"))
            return Reply(new ChatMessage(ChatRole.Assistant, "Done."));

        var offered = options?.Tools?.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        if (offered.Contains(typed))
            return Reply(Call(typed, new Dictionary<string, object?>(turn.ScriptedArgs)));

        if (offered.Contains(McpTypedToolSurface.FindToolsName) && !calls.Any(c => c.Name == McpTypedToolSurface.FindToolsName))
            return Reply(Call(McpTypedToolSurface.FindToolsName, new() { ["query"] = turn.FindQuery }));

        return Reply(Call("mcp_invoke_tool", new()
        {
            ["server_name"] = turn.Server,
            ["tool_name"] = turn.Tool,
            ["arguments"] = new Dictionary<string, object?>(turn.ScriptedArgs)
        }));
    }

    private static ChatMessage Call(string name, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments)]);

    private static Task<ChatResponse> Reply(ChatMessage message) =>
        Task.FromResult(new ChatResponse(message)
        {
            ModelId = ModelId,
            Usage = new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 }
        });

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", null, ModelId) : null;

    public void Dispose() { }
}
