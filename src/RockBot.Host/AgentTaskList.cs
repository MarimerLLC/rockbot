namespace RockBot.Host;

/// <summary>
/// Per-<see cref="AgentLoopRunner.RunAsync"/> in-memory task list. Holds the agent's
/// committed plan as structured state so it survives context trimming — the list is
/// rendered into a system message that <see cref="AgentLoopRunner"/> refreshes from
/// this state on each iteration, rather than living only in the chat history where
/// trimming or aging can degrade it.
/// </summary>
internal sealed class AgentTaskList
{
    public const string StatusPending = "pending";
    public const string StatusInProgress = "in_progress";
    public const string StatusCompleted = "completed";

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        StatusPending, StatusInProgress, StatusCompleted
    };

    private readonly Lock _gate = new();
    private readonly List<TaskItem> _items = [];

    public sealed record TaskItem(int Id, string Description, string Status);

    public bool IsEmpty
    {
        get { lock (_gate) return _items.Count == 0; }
    }

    public bool HasUnfinishedItems
    {
        get
        {
            lock (_gate)
            {
                foreach (var item in _items)
                {
                    if (!string.Equals(item.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// Replaces the current list with the given items. Each item gets a sequential
    /// id starting at 1 and an initial status of <see cref="StatusPending"/>. Returns
    /// the newly created items.
    /// </summary>
    public IReadOnlyList<TaskItem> CreateOrReplace(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        lock (_gate)
        {
            _items.Clear();
            var id = 1;
            foreach (var description in items)
            {
                if (string.IsNullOrWhiteSpace(description))
                    continue;
                _items.Add(new TaskItem(id++, description.Trim(), StatusPending));
            }
            return _items.ToArray();
        }
    }

    /// <summary>
    /// Updates a single item's status. Returns the updated item, or null when no
    /// item with the given id exists. Throws <see cref="ArgumentException"/> for
    /// unknown status values.
    /// </summary>
    public TaskItem? Update(int id, string status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!ValidStatuses.Contains(status))
            throw new ArgumentException(
                $"Invalid status '{status}'. Valid statuses: {string.Join(", ", ValidStatuses)}.",
                nameof(status));

        var normalized = status.ToLowerInvariant();

        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == id)
                {
                    var updated = _items[i] with { Status = normalized };
                    _items[i] = updated;
                    return updated;
                }
            }
            return null;
        }
    }

    public IReadOnlyList<TaskItem> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }
}
