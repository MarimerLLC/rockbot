using System.Runtime.CompilerServices;

namespace RockBot.UserProxy.ChatState;

/// <summary>The kinds of work the activity panel (and the header's activity slots) shows.</summary>
public enum ActivityKind
{
    /// <summary>RockBot itself: the user's turns.</summary>
    Agent,

    /// <summary>Subagents and workers.</summary>
    Subagent,

    /// <summary>Calls from other agents (inbound A2A).</summary>
    A2A,

    /// <summary>Background tasks: system jobs and the user's own scheduled tasks.</summary>
    Scheduled,
}

/// <summary>One piece of work in the <see cref="ActivityDigest"/>: running, or finished recently.</summary>
public sealed record ActivityItem
{
    /// <summary>A stable id: its activity log's message id, else its result's.</summary>
    public required string Id { get; init; }

    public required ActivityKind Kind { get; init; }

    /// <summary>
    /// The run's agent name as the chat keeps it: <c>subagent-{taskId}</c>, <c>worker-{taskId}</c>, a
    /// remote agent's or a task's name. Null for RockBot's own turns.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>A short name to show: the task id for a subagent, <c>worker {taskId}</c> for a worker, else <see cref="AgentName"/>.</summary>
    public string? DisplayName { get; init; }

    public bool IsWorker { get; init; }

    public bool IsRunning { get; init; }

    public DateTime StartedAt { get; init; }

    /// <summary>When it finished, as far as the chat saw; null while running.</summary>
    public DateTime? FinishedAt { get; init; }

    /// <summary>
    /// What it's doing, or how it ended: the latest line of its log. For RockBot's running turn,
    /// the agent's live status text when there is one.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>Its task list while it runs, if it reported one.</summary>
    public TaskListView? Plan { get; init; }

    /// <summary>Its activity log (entries and tool calls), if the chat saw one.</summary>
    public ChatMessage? Log { get; init; }

    /// <summary>The reply it produced, if the chat saw one.</summary>
    public ChatMessage? Result { get; init; }
}

/// <summary>The running and recently finished work of one <see cref="ActivityKind"/>.</summary>
/// <param name="Running">In the order it started.</param>
/// <param name="Finished">Most recent first.</param>
public sealed record ActivityGroup(IReadOnlyList<ActivityItem> Running, IReadOnlyList<ActivityItem> Finished)
{
    public static ActivityGroup Empty { get; } = new([], []);
}

/// <summary>
/// What the agent is doing and has done, per <see cref="ActivityKind"/>, projected from a
/// <see cref="ChatViewState"/> for the activity panel (#828). It reads the same activity logs
/// and replies the Detailed transcript draws, so the two always agree.
/// </summary>
public sealed record ActivityDigest(
    ActivityGroup Agent,
    ActivityGroup Subagents,
    ActivityGroup A2A,
    ActivityGroup Scheduled)
{
    /// <summary>How many finished items each kind keeps by default.</summary>
    public const int DefaultMaxFinished = 5;

    public static ActivityDigest Empty { get; } =
        new(ActivityGroup.Empty, ActivityGroup.Empty, ActivityGroup.Empty, ActivityGroup.Empty);

    public ActivityGroup this[ActivityKind kind] => kind switch
    {
        ActivityKind.Agent => Agent,
        ActivityKind.Subagent => Subagents,
        ActivityKind.A2A => A2A,
        ActivityKind.Scheduled => Scheduled,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The kind whose work a message belongs to, or null for the user's own messages, errors and the like.</summary>
    public static ActivityKind? KindOf(MessageCategory category) => category switch
    {
        MessageCategory.PrimaryProgress or MessageCategory.PrimaryFinal => ActivityKind.Agent,
        MessageCategory.SubagentActivity => ActivityKind.Subagent,
        MessageCategory.A2AActivity => ActivityKind.A2A,
        MessageCategory.ScheduledSystem or MessageCategory.ScheduledUser => ActivityKind.Scheduled,
        _ => null,
    };

    /// <param name="utcNow">The time now, for the busy state.</param>
    /// <param name="maxFinished">How many finished items to keep per kind.</param>
    /// <remarks>
    /// Front ends call this on every render. The state is immutable and the digest depends on
    /// <paramref name="utcNow"/> only through the busy state, so it is cached per state instance.
    /// </remarks>
    public static ActivityDigest From(ChatViewState state, DateTime utcNow, int maxFinished = DefaultMaxFinished)
    {
        var busy = state.IsBusy(utcNow);
        if (Cache.TryGetValue(state, out var hit) && hit.Busy == busy && hit.MaxFinished == maxFinished)
            return hit.Digest;

        var digest = new ActivityDigest(
            AgentGroup(state, busy, maxFinished),
            RunGroup(state, ActivityKind.Subagent, maxFinished),
            RunGroup(state, ActivityKind.A2A, maxFinished),
            RunGroup(state, ActivityKind.Scheduled, maxFinished));
        Cache.AddOrUpdate(state, new CacheEntry(busy, maxFinished, digest));
        return digest;
    }

    private sealed record CacheEntry(bool Busy, int MaxFinished, ActivityDigest Digest);

    /// <summary>Keyed by state instance (reference identity); an entry goes when its state does.</summary>
    private static readonly ConditionalWeakTable<ChatViewState, CacheEntry> Cache = new();

    // ── RockBot's own turns ─────────────────────────────────────────────────

    /// <summary>
    /// One item per primary progress log, each with the first reply of RockBot's that follows
    /// it before the user's next message. While the agent is busy the latest turn is running,
    /// with the live status text and the turn's plan, even before it has logged anything.
    /// </summary>
    private static ActivityGroup AgentGroup(ChatViewState state, bool busy, int maxFinished)
    {
        var messages = state.Messages;
        var items = new List<ActivityItem>();
        for (var i = 0; i < messages.Count; i++)
        {
            var log = messages[i];
            if (!log.IsActivityLog || log.Category != MessageCategory.PrimaryProgress)
                continue;

            var result = FollowingPrimaryReply(messages, i);
            var open = state.IsActivityLogOpen(log.MessageId);
            items.Add(new ActivityItem
            {
                Id = log.MessageId,
                Kind = ActivityKind.Agent,
                IsRunning = open,
                StartedAt = log.Timestamp,
                FinishedAt = open ? null : result?.Timestamp ?? LastEntryTime(log),
                Status = log.Content,
                Log = log,
                Result = result,
            });
        }

        var running = items.Where(i => i.IsRunning).ToList();
        var finished = items.Where(i => !i.IsRunning).ToList();

        if (busy || running.Count > 0)
        {
            // One running turn: the busy turn is the latest open log, or one with nothing logged yet.
            var current = running.Count > 0 ? running[^1] : null;
            finished.AddRange(running.Take(running.Count - 1));
            state.TaskLists.TryGetValue(ReplySourceKind.Primary, out var plan);
            current = (current ?? new ActivityItem
            {
                Id = state.CurrentTurn?.TurnId ?? "agent-turn",
                Kind = ActivityKind.Agent,
                IsRunning = true,
                // Busy with no turn means a form answer is waiting for its follow-up: since it was sent.
                StartedAt = state.CurrentTurn?.StartedAt
                    ?? (state.FormWaits.IsEmpty ? DateTime.MinValue : state.FormWaits.Values.Min()),
            }) with
            {
                Status = state.ThinkingMessage ?? current?.Status,
                Plan = plan,
            };
            running = [current];
        }

        return new ActivityGroup(running, Latest(finished, maxFinished));
    }

    /// <summary>RockBot's first reply after <paramref name="index"/>, before the user's next message.</summary>
    private static ChatMessage? FollowingPrimaryReply(IReadOnlyList<ChatMessage> messages, int index)
    {
        for (var i = index + 1; i < messages.Count; i++)
        {
            if (ChatVisibility.IsPrimaryReply(messages[i]))
                return messages[i];
            if (messages[i].Category == MessageCategory.UserInput)
                return null;
        }
        return null;
    }

    // ── Subagents, A2A calls and background tasks ───────────────────────────

    /// <summary>
    /// One item per run: each activity log of the kind starts one, and a reply joins the latest
    /// run of the same agent name that has no reply yet. A reply with no run (after a reload,
    /// which brings back replies but not logs) is a finished item on its own.
    /// </summary>
    private static ActivityGroup RunGroup(ChatViewState state, ActivityKind kind, int maxFinished)
    {
        var items = new List<ActivityItem>();
        var latestByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in state.Messages)
        {
            if (message.IsFromUser || KindOf(message.Category) != kind)
                continue;

            var name = message.RunAgentName ?? message.AgentName;
            var key = $"{message.Category}:{name}";

            if (message.IsActivityLog)
            {
                var open = state.IsActivityLogOpen(message.MessageId);
                latestByName[key] = items.Count;
                items.Add(NewRun(kind, name, message.MessageId, message.Timestamp) with
                {
                    IsRunning = open,
                    FinishedAt = open ? null : LastEntryTime(message),
                    Status = message.Content,
                    Log = message,
                    Plan = open && kind == ActivityKind.Subagent && ChatReducer.SubagentTaskId(name) is { } taskId
                        && state.TaskLists.TryGetValue(taskId, out var plan) ? plan : null,
                });
                continue;
            }

            // A progress line that isn't in a log adds nothing a log doesn't.
            if (message.IsInterim)
                continue;

            if (latestByName.TryGetValue(key, out var at) && items[at].Result is null)
            {
                items[at] = items[at] with
                {
                    Result = message,
                    FinishedAt = items[at].IsRunning ? null : message.Timestamp,
                };
            }
            else
            {
                latestByName[key] = items.Count;
                items.Add(NewRun(kind, name, message.MessageId, message.Timestamp) with
                {
                    FinishedAt = message.Timestamp,
                    Result = message,
                });
            }
        }

        return new ActivityGroup(
            items.Where(i => i.IsRunning).OrderBy(i => i.StartedAt).ToList(),
            Latest(items.Where(i => !i.IsRunning), maxFinished));
    }

    private static ActivityItem NewRun(ActivityKind kind, string? name, string id, DateTime startedAt)
    {
        var isWorker = name?.StartsWith("worker-", StringComparison.OrdinalIgnoreCase) == true;
        return new ActivityItem
        {
            Id = id,
            Kind = kind,
            AgentName = name,
            IsWorker = isWorker,
            DisplayName = ChatReducer.SubagentTaskId(name) is { } taskId ? (isWorker ? $"worker {taskId}" : taskId) : name,
            StartedAt = startedAt,
        };
    }

    private static DateTime LastEntryTime(ChatMessage log) =>
        log.ActivityLogEntries.Count > 0 ? log.ActivityLogEntries[^1].Timestamp : log.Timestamp;

    /// <summary>The <paramref name="max"/> most recently finished, most recent first.</summary>
    private static IReadOnlyList<ActivityItem> Latest(IEnumerable<ActivityItem> finished, int max) =>
        finished
            .Select((item, order) => (item, order))
            .OrderByDescending(x => x.item.FinishedAt ?? x.item.StartedAt)
            .ThenByDescending(x => x.order)
            .Take(max)
            .Select(x => x.item)
            .ToList();
}
