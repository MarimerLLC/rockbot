using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Subagent;

/// <summary>
/// Tools for writing large outputs from a subagent into the primary session's working memory,
/// so the primary agent can access them during follow-up conversation.
/// Entries are namespaced as "subagent:{taskId}:{key}" and expire after the configured TTL.
/// </summary>
internal sealed class SubagentSharedOutputFunctions
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    public IList<AITool> Tools { get; }

    private readonly IWorkingMemory _workingMemory;
    private readonly string _primarySessionId;
    private readonly string _taskId;
    private readonly ILogger _logger;

    public SubagentSharedOutputFunctions(
        IWorkingMemory workingMemory,
        string primarySessionId,
        string taskId,
        ILogger logger)
    {
        _workingMemory = workingMemory;
        _primarySessionId = primarySessionId;
        _taskId = taskId;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(WriteSharedOutput),
            AIFunctionFactory.Create(ReadSharedOutput),
            AIFunctionFactory.Create(ListSharedOutputs),
        ];
    }

    [Description("Write a large output (report, document, structured data) into shared memory " +
                 "where the primary agent can retrieve it. Entries expire automatically after 1 hour. " +
                 "Use descriptive keys like 'email-report', 'summary', 'results'. " +
                 "Always mention the key in your final report so the primary agent knows where to look.")]
    public async Task<string> WriteSharedOutput(
        [Description("Key to store this output under (e.g. 'email-report', 'search-results')")] string key,
        [Description("The output content to store")] string value)
    {
        var fullKey = $"subagent:{_taskId}:{key}";
        _logger.LogInformation("WriteSharedOutput(session={Session}, key={Key})", _primarySessionId, fullKey);
        await _workingMemory.SetAsync(_primarySessionId, fullKey, value, ttl: DefaultTtl, category: "subagent-output");
        return $"Stored under key '{fullKey}' in primary session working memory (expires in 30 minutes).";
    }

    [Description("Read input data that the primary agent wrote to shared memory before spawning this task.")]
    public async Task<string> ReadSharedOutput(
        [Description("Key to read (e.g. 'subagent:{taskId}:input')")] string key)
    {
        _logger.LogInformation("ReadSharedOutput(session={Session}, key={Key})", _primarySessionId, key);
        var value = await _workingMemory.GetAsync(_primarySessionId, key);
        return value ?? $"No entry found for key '{key}'.";
    }

    [Description("List all shared output keys written by this subagent task.")]
    public async Task<string> ListSharedOutputs()
    {
        var prefix = $"subagent:{_taskId}:";
        var all = await _workingMemory.ListAsync(_primarySessionId);
        var mine = all.Where(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

        if (mine.Count == 0)
            return "No shared outputs written yet.";

        return string.Join("\n", mine.Select(e => $"- {e.Key}"));
    }
}
