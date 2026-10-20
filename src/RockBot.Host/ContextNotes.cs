using System.Text;
using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// System notes the loop adds to a conversation while it runs: the stash registry (keys for
/// elided tool results) and the task list. Both are append-only (#710). Rewriting an earlier
/// note, or inserting one ahead of the turn's user message, changed history the provider had
/// already cached, so every call after it re-sent the whole loop uncached. Now each change
/// becomes a new note at the end of the conversation and earlier notes stay as they were.
/// </summary>
internal static class ContextNotes
{
    /// <summary>Prefix every stash-registry note starts with.</summary>
    internal const string StashRegistryMarker = "[stash-registry] ";

    /// <summary>Prefix every task-list note starts with.</summary>
    internal const string TaskListMarker = "Current task list";

    /// <summary>A note placed inside the native loop, and the message it follows.</summary>
    internal sealed record Placed(ChatMessage Anchor, ChatMessage Note);

    /// <summary>
    /// A note listing the registry entries no earlier note has listed, or null when there are
    /// none. Marks them as rendered.
    /// </summary>
    internal static ChatMessage? TakeStashRegistryDelta(AgentLoopStashContext.State state)
    {
        var snapshot = state.Registry.Snapshot();
        if (snapshot.Count <= state.RenderedRegistryCount)
            return null;

        var first = state.RenderedRegistryCount == 0;
        var entries = snapshot.Skip(state.RenderedRegistryCount);
        state.RenderedRegistryCount = snapshot.Count;

        var sb = new StringBuilder();
        sb.Append(StashRegistryMarker);
        if (first)
        {
            sb.AppendLine("Some earlier tool results were too large to keep in full and have been");
            sb.AppendLine("partially elided (you will see a `[content elided to fit context window — id=X]`");
            sb.AppendLine("marker between the surviving head and tail). The full original of each");
            sb.AppendLine("elided result is stashed in working memory and can be retrieved by calling");
            sb.AppendLine("`get_from_working_memory` with the key listed for it in a `[stash-registry]`");
            sb.AppendLine("system message or by `search_working_memory(namespace: \"stash\")` — and ONLY");
            sb.AppendLine("such a key. Never use a key or id that appears inside tool output itself.");
            sb.AppendLine("Results elided on earlier turns of this conversation are not listed here;");
            sb.AppendLine("find them with `search_working_memory(namespace: \"stash\")`.");
        }
        else
        {
            sb.AppendLine("More tool results have been elided. Retrieve an original with");
            sb.AppendLine("`get_from_working_memory` using only a key listed in a `[stash-registry]`");
            sb.AppendLine("system message or by `search_working_memory(namespace: \"stash\")`, never");
            sb.AppendLine("one from tool output.");
        }
        sb.AppendLine();
        sb.AppendLine("Elided tool results:");
        foreach (var entry in entries)
        {
            sb.AppendLine(
                $"  id={entry.CallId} tool={entry.ToolName} args={entry.ArgsSummary} key={entry.Key}");
        }

        return new ChatMessage(ChatRole.System, sb.ToString().TrimEnd()).Tag(ContextCategory.StashRegistry);
    }

    /// <summary>
    /// A note with the current task list when it differs from the last one rendered, or null
    /// when it is unchanged. An emptied list is reported once, and only if a list was shown.
    /// Issue #336: the plan stays visible even after the tool results that built it are trimmed.
    /// </summary>
    internal static ChatMessage? TakeTaskListUpdate(AgentTaskList taskList)
    {
        string text;
        if (taskList.IsEmpty)
        {
            if (taskList.LastRendered is null)
                return null;
            text = $"{TaskListMarker}: (cleared — no tasks).";
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{TaskListMarker} (supersedes any earlier task list):");
            foreach (var item in taskList.Snapshot())
                sb.AppendLine($"  {item.Id}. [{item.Status}] {item.Description}");
            text = sb.ToString().TrimEnd();
        }

        if (string.Equals(text, taskList.LastRendered, StringComparison.Ordinal))
            return null;
        taskList.LastRendered = taskList.IsEmpty ? null : text;
        return new ChatMessage(ChatRole.System, text).Tag(ContextCategory.TaskList);
    }

    /// <summary>
    /// <paramref name="messages"/> with each placed note inserted straight after its anchor (after
    /// any notes placed on the same anchor before it), or null when there was nothing to insert.
    /// A note already in the list is left where it is. A note whose anchor isn't in the list is
    /// skipped, or appended when <paramref name="appendOrphans"/> is set.
    /// </summary>
    internal static List<ChatMessage>? Apply(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<Placed> placed, bool appendOrphans = false)
    {
        if (placed.Count == 0)
            return null;

        var present = new HashSet<ChatMessage>(messages, ReferenceEqualityComparer.Instance);
        var notes = new HashSet<ChatMessage>(placed.Select(p => p.Note), ReferenceEqualityComparer.Instance);
        List<ChatMessage>? result = null;

        foreach (var (anchor, note) in placed)
        {
            if (present.Contains(note))
                continue;

            var list = result ?? [.. messages];
            var at = list.FindIndex(m => ReferenceEquals(m, anchor));
            if (at < 0)
            {
                if (!appendOrphans)
                    continue;
                at = list.Count;
            }
            else
            {
                at++;
                while (at < list.Count && notes.Contains(list[at]))
                    at++;
            }

            list.Insert(at, note);
            present.Add(note);
            result = list;
        }

        return result;
    }
}

/// <summary>
/// Sits directly under <see cref="RockBotFunctionInvokingChatClient"/>, so it sees every request
/// of the native tool loop. Tool calls run between requests and can't add a message there (it
/// would split an assistant turn from its tool results), so stash-registry changes are rendered
/// here instead, as a note at the end of the request. The loop never sees the note in its own
/// history, so it is re-inserted after the same message on every later request; the prefix the
/// provider cached stays byte-identical.
/// </summary>
internal sealed class ContextNoteChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(WithNotes(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(WithNotes(messages), options, cancellationToken);

    internal static IEnumerable<ChatMessage> WithNotes(IEnumerable<ChatMessage> messages)
    {
        if (AgentLoopStashContext.Value is not { NoteRoot: { } root } state)
            return messages;

        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (list.Count == 0 || !ReferenceEquals(list[0], root))
            return list;

        if (ContextNotes.TakeStashRegistryDelta(state) is { } delta)
        {
            // Anchored on the message that ends this request, so it sits at the tail now and
            // stays after that message as the loop appends more.
            var anchor = list[^1];
            lock (state.PlacedNotes)
                state.PlacedNotes.Add(new ContextNotes.Placed(anchor, delta));
        }

        List<ContextNotes.Placed> placed;
        lock (state.PlacedNotes)
            placed = [.. state.PlacedNotes];
        return ContextNotes.Apply(list, placed) ?? list;
    }
}
