using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>How downstream (MCP) tools are offered to the model as typed tools.</summary>
public enum TypedToolMode
{
    /// <summary>No typed tools; every downstream call goes through the generic gateway tool.</summary>
    Off,

    /// <summary>Every downstream tool is a typed tool in every tool list.</summary>
    Eager,

    /// <summary>
    /// Typed tools join a session's tool list only once the session has searched for them, or
    /// called one by name (#612).
    /// </summary>
    Lazy
}

/// <summary>
/// The typed downstream tool surface as host-level code sees it: the mode, for prompts and hints
/// that name tools, and, in <see cref="TypedToolMode.Lazy"/>, the tools each session has
/// activated (#612).
/// <para>
/// Activations are keyed by the tool session id that a run's registry tools carry
/// (<see cref="ISessionBoundTool"/>), the same id their executors see on each call. They join a
/// run's tool list only when that list already holds <see cref="LoaderToolName"/>, so a run whose
/// tool profile leaves out the gateway never gains typed tools this way.
/// </para>
/// </summary>
public interface ITypedToolSurface
{
    TypedToolMode Mode { get; }

    /// <summary>The tool that searches for and activates typed tools (<c>mcp_find_tools</c>).</summary>
    string LoaderToolName { get; }

    /// <summary>The typed tools <paramref name="toolSessionId"/> has activated, oldest first.</summary>
    IReadOnlyList<AIFunction> GetActivated(string toolSessionId);

    /// <summary>
    /// The model called <paramref name="toolName"/>, which isn't in its tool list. When that is a
    /// valid typed tool, activates it for <paramref name="toolSessionId"/> and returns it;
    /// otherwise null.
    /// </summary>
    AIFunction? ActivateByName(string toolSessionId, string toolName);
}

/// <summary>An <see cref="AIFunction"/> whose calls carry a tool session id.</summary>
public interface ISessionBoundTool
{
    string? SessionId { get; }
}

/// <summary>
/// Keeps a run's tool list in step with the session's typed-tool activations. The surface is
/// ambient per agent loop run (set by <see cref="AgentLoopRunner.RunAsync"/>), so the singleton
/// chat-client pipeline can reach it without constructor plumbing.
/// </summary>
public static class TypedToolSurfaceContext
{
    private static readonly AsyncLocal<ITypedToolSurface?> Current = new();

    /// <summary>The surface for the current run, or null.</summary>
    public static ITypedToolSurface? Surface => Current.Value;

    public static IDisposable Set(ITypedToolSurface? surface)
    {
        var previous = Current.Value;
        Current.Value = surface;
        return new Scope(previous);
    }

    /// <summary>
    /// Adds the session's activated typed tools that <paramref name="options"/> doesn't list yet.
    /// Returns how many were added.
    /// </summary>
    public static int AddActivated(ChatOptions? options)
    {
        if (!TryGetLoader(options, out var surface, out var loader, out var sessionId))
            return 0;

        var present = Names(options!.Tools!);
        var added = 0;
        foreach (var tool in surface.GetActivated(sessionId))
        {
            if (present.Add(tool.Name))
            {
                Append(options, loader, tool);
                added++;
            }
        }
        return added;
    }

    /// <summary>
    /// Resolves a call to <paramref name="toolName"/>, which isn't in <paramref name="options"/>,
    /// as a typed tool: activates it and adds it to the list. Returns the added tool, or null.
    /// </summary>
    public static AIFunction? TryActivate(ChatOptions? options, string toolName)
    {
        if (!TryGetLoader(options, out var surface, out var loader, out var sessionId))
            return null;

        if (surface.ActivateByName(sessionId, toolName) is not { } tool)
            return null;

        return Append(options!, loader, tool);
    }

    private static bool TryGetLoader(
        ChatOptions? options, out ITypedToolSurface surface, out AIFunction loader, out string sessionId)
    {
        surface = null!;
        loader = null!;
        sessionId = null!;

        if (Current.Value is not { Mode: TypedToolMode.Lazy } current || options?.Tools is not { Count: > 0 } tools)
            return false;

        var found = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == current.LoaderToolName);
        if (found?.GetService<ISessionBoundTool>()?.SessionId is not { Length: > 0 } id)
            return false;

        surface = current;
        loader = found;
        sessionId = id;
        return true;
    }

    // An added tool is wrapped the way the loader is, so its results get the same treatment
    // (chunking into working memory) as every other registry tool in the run.
    private static AIFunction Append(ChatOptions options, AIFunction loader, AIFunction tool)
    {
        var wrapped = loader is ChunkingAIFunction chunking ? chunking.WithInner(tool) : tool;

        if (options.Tools!.IsReadOnly)
            options.Tools = [.. options.Tools, wrapped];
        else
            options.Tools.Add(wrapped);

        return wrapped;
    }

    private static HashSet<string> Names(IList<AITool> tools) =>
        tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    private sealed class Scope(ITypedToolSurface? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Sits under <see cref="RockBotFunctionInvokingChatClient"/> so typed-tool activations take
/// effect within the turn (#612). Before each request it adds the tools the session activated
/// since the last one — <c>mcp_find_tools</c> in iteration N makes its results callable in
/// iteration N+1. After each response it resolves calls to typed tools the list doesn't hold
/// (a name from a skill or from memory), so the function-invoking loop finds and runs them
/// instead of answering "unknown tool". Both act on the options instance the loop dispatches
/// against. A no-op outside lazy mode.
/// </summary>
internal sealed class TypedToolSurfaceChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        TypedToolSurfaceContext.AddActivated(options);

        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        if (options?.Tools is { Count: > 0 } tools)
        {
            var present = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var call in response.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()))
            {
                if (!present.Contains(call.Name) && TypedToolSurfaceContext.TryActivate(options, call.Name) is not null)
                    present.Add(call.Name);
            }
        }

        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        TypedToolSurfaceContext.AddActivated(options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
