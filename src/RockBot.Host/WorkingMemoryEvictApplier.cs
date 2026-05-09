using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Applies a <see cref="RepairTarget.WorkingMemoryEvict"/> change by deleting
/// working-memory entries by key or key-prefix. Eviction is irreversible —
/// the applier returns no <c>Revert</c> callback.
/// </summary>
internal sealed class WorkingMemoryEvictApplier : IRepairTargetApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IWorkingMemory _workingMemory;
    private readonly ILogger<WorkingMemoryEvictApplier> _logger;

    public WorkingMemoryEvictApplier(IWorkingMemory workingMemory, ILogger<WorkingMemoryEvictApplier> logger)
    {
        _workingMemory = workingMemory ?? throw new ArgumentNullException(nameof(workingMemory));
        _logger = logger;
    }

    public RepairTarget Target => RepairTarget.WorkingMemoryEvict;

    public async Task<RepairApplyOutcome> ApplyAsync(RepairTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var change = ticket.Change.Deserialize<WorkingMemoryEvictChange>(JsonOptions)
            ?? throw new ArgumentException("WorkingMemoryEvict change is empty.", nameof(ticket));

        var hasPrefix = !string.IsNullOrWhiteSpace(change.KeyPrefix);
        var hasKeys = change.Keys is { Count: > 0 };

        if (!hasPrefix && !hasKeys)
            throw new ArgumentException("WorkingMemoryEvict change requires 'keyPrefix' or 'keys'.", nameof(ticket));

        var evictedKeys = new List<string>();

        if (hasPrefix)
        {
            var matches = await _workingMemory.ListAsync(change.KeyPrefix);
            foreach (var entry in matches)
                evictedKeys.Add(entry.Key);

            await _workingMemory.ClearAsync(change.KeyPrefix);
        }

        if (hasKeys)
        {
            foreach (var key in change.Keys!)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                await _workingMemory.DeleteAsync(key);
                evictedKeys.Add(key);
            }
        }

        var diff = JsonSerializer.SerializeToElement(new
        {
            keyPrefix = change.KeyPrefix,
            keys = change.Keys,
            evictedCount = evictedKeys.Count,
            sampleKeys = evictedKeys.Take(10).ToList(),
        }, JsonOptions);

        _logger.LogInformation(
            "WorkingMemoryEvictApplier: evicted {Count} key(s){Prefix}",
            evictedKeys.Count,
            hasPrefix ? $" prefix='{change.KeyPrefix}'" : string.Empty);

        return new RepairApplyOutcome(diff, Revert: null);
    }

    internal sealed class WorkingMemoryEvictChange
    {
        public string? KeyPrefix { get; set; }
        public List<string>? Keys { get; set; }
    }
}
