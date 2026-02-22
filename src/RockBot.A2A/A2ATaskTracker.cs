using System.Collections.Concurrent;

namespace RockBot.A2A;

/// <summary>
/// Thread-safe tracker of in-flight A2A tasks dispatched by the primary agent.
/// </summary>
internal sealed class A2ATaskTracker
{
    private readonly ConcurrentDictionary<string, PendingA2ATask> _tasks = new(StringComparer.Ordinal);

    public void Track(PendingA2ATask task) =>
        _tasks[task.TaskId] = task;

    public bool TryRemove(string taskId, out PendingA2ATask? task) =>
        _tasks.TryRemove(taskId, out task);

    public bool TryGet(string taskId, out PendingA2ATask? task) =>
        _tasks.TryGetValue(taskId, out task);

    public IReadOnlyList<PendingA2ATask> ListActive() =>
        _tasks.Values.ToList();
}
