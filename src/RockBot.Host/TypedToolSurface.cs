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
    Lazy,

    /// <summary>
    /// Lazy, and once the session has called a server, all of that server's typed tools stay in
    /// its tool list (#613).
    /// </summary>
    Pinned
}

/// <summary>
/// The typed downstream tool surface as host-level code sees it: the mode each model tier runs
/// in (#613), for shaping each run's tool list and for prompts and hints that name tools, and, in
/// <see cref="TypedToolMode.Lazy"/> and <see cref="TypedToolMode.Pinned"/>, the tools each
/// session has activated (#612).
/// <para>
/// Activations are keyed by the tool session id that a run's registry tools carry
/// (<see cref="ISessionBoundTool"/>), the same id their executors see on each call. They join a
/// run's tool list only when that list already holds <see cref="LoaderToolName"/>, so a run whose
/// tool profile leaves out the gateway never gains typed tools this way.
/// </para>
/// </summary>
public interface ITypedToolSurface
{
    /// <summary>The mode a run on <paramref name="tier"/> uses.</summary>
    TypedToolMode ModeFor(ModelTier tier);

    /// <summary>The tool that searches for and activates typed tools (<c>mcp_find_tools</c>).</summary>
    string LoaderToolName { get; }

    /// <summary>True when <paramref name="toolName"/> is a typed downstream tool.</summary>
    bool IsTypedTool(string toolName);

    /// <summary>Typed tools join a run's list only while it holds at most this many tools.</summary>
    int MaxToolsPerRequest => 120;

    /// <summary>
    /// The typed tools <paramref name="toolSessionId"/> has activated, oldest first; in
    /// <see cref="TypedToolMode.Pinned"/>, followed by every typed tool of the servers it has called.
    /// </summary>
    IReadOnlyList<AIFunction> GetActivated(string toolSessionId, TypedToolMode mode);

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
    private static readonly AsyncLocal<Run?> Current = new();

    /// <summary>The surface for the current run, or null.</summary>
    public static ITypedToolSurface? Surface => Current.Value?.Surface;

    /// <summary>The current run's mode; null outside a run.</summary>
    public static TypedToolMode? Mode => Current.Value?.Mode;

    /// <summary>True when the current run adds typed tools as the session activates them.</summary>
    public static bool IsActivating => Mode is TypedToolMode.Lazy or TypedToolMode.Pinned;

    /// <summary>Makes <paramref name="surface"/>, in the mode for <paramref name="tier"/>, ambient for a run.</summary>
    public static IDisposable Set(ITypedToolSurface? surface, ModelTier tier)
    {
        var previous = Current.Value;
        Current.Value = surface is null ? null : new Run(surface, surface.ModeFor(tier));
        return new Scope(previous);
    }

    /// <summary>
    /// Fits a run's tool list to its mode (#613). The registry holds every typed tool when any
    /// tier runs eager, and <c>mcp_find_tools</c> when any tier activates; a run drops the ones
    /// its own mode doesn't use. Returns how many were removed.
    /// </summary>
    public static int Shape(ChatOptions? options)
    {
        if (Current.Value is not { } run || options?.Tools is not { Count: > 0 } tools)
            return 0;

        var dropTyped = run.Mode != TypedToolMode.Eager;
        var dropLoader = run.Mode is TypedToolMode.Off or TypedToolMode.Eager;
        if (!tools.Any(t => Drops(t.Name)))
            return 0;

        var kept = tools.Where(t => !Drops(t.Name)).ToList();
        var removed = tools.Count - kept.Count;
        options.Tools = kept;
        return removed;

        bool Drops(string name) =>
            (dropLoader && name == run.Surface.LoaderToolName) || (dropTyped && run.Surface.IsTypedTool(name));
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
        foreach (var tool in surface.GetActivated(sessionId, Current.Value!.Mode))
        {
            // Over the provider's tool cap the whole request fails, not just the extra tool.
            if (options.Tools!.Count >= surface.MaxToolsPerRequest)
                break;
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
        if (!TryGetLoader(options, out var surface, out var loader, out var sessionId)
            || options!.Tools!.Count >= surface.MaxToolsPerRequest)
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

        if (Current.Value is not { Mode: TypedToolMode.Lazy or TypedToolMode.Pinned } run
            || options?.Tools is not { Count: > 0 } tools)
            return false;

        var current = run.Surface;
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

    private sealed record Run(ITypedToolSurface Surface, TypedToolMode Mode);

    private sealed class Scope(Run? previous) : IDisposable
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
/// against. A no-op unless the run activates typed tools.
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
