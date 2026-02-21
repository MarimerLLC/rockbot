using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Subagent;

/// <summary>
/// Whiteboard tools for the primary agent. Unlike <see cref="WhiteboardFunctions"/>,
/// the board_id is an explicit parameter so the primary agent can access any
/// subagent's board by passing the task_id returned from spawn_subagent.
/// </summary>
public sealed class PrimaryWhiteboardFunctions
{
    public IList<AITool> Tools { get; }

    private readonly IWhiteboardMemory _whiteboard;
    private readonly ILogger _logger;

    public PrimaryWhiteboardFunctions(IWhiteboardMemory whiteboard, ILogger logger)
    {
        _whiteboard = whiteboard;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(WhiteboardWrite),
            AIFunctionFactory.Create(WhiteboardRead),
            AIFunctionFactory.Create(WhiteboardList),
            AIFunctionFactory.Create(WhiteboardDelete)
        ];
    }

    [Description("Write a key-value pair to a subagent's whiteboard. " +
                 "Use the task_id from spawn_subagent as the board_id. " +
                 "The subagent can read this data using its own whiteboard tools.")]
    public async Task<string> WhiteboardWrite(
        [Description("The task_id of the subagent (from spawn_subagent)")] string board_id,
        [Description("Short descriptive key (e.g. 'urls-to-process', 'input-data')")] string key,
        [Description("The data to store")] string value)
    {
        _logger.LogInformation("PrimaryWhiteboardWrite(board={BoardId}, key={Key})", board_id, key);
        await _whiteboard.WriteAsync(board_id, key, value);
        return $"Wrote to whiteboard board '{board_id}', key '{key}'.";
    }

    [Description("Read a value from a subagent's whiteboard. " +
                 "Use the task_id from spawn_subagent as the board_id.")]
    public async Task<string> WhiteboardRead(
        [Description("The task_id of the subagent (from spawn_subagent)")] string board_id,
        [Description("The key to read")] string key)
    {
        _logger.LogInformation("PrimaryWhiteboardRead(board={BoardId}, key={Key})", board_id, key);
        var value = await _whiteboard.ReadAsync(board_id, key);
        return value ?? $"No whiteboard entry found for board '{board_id}', key '{key}'.";
    }

    [Description("List all keys and values on a subagent's whiteboard. " +
                 "Use the task_id from spawn_subagent as the board_id.")]
    public async Task<string> WhiteboardList(
        [Description("The task_id of the subagent (from spawn_subagent)")] string board_id)
    {
        _logger.LogInformation("PrimaryWhiteboardList(board={BoardId})", board_id);
        var entries = await _whiteboard.ListAsync(board_id);

        if (entries.Count == 0)
            return $"Whiteboard board '{board_id}' is empty.";

        var sb = new StringBuilder();
        sb.AppendLine($"Whiteboard '{board_id}' ({entries.Count} entries):");
        foreach (var (key, value) in entries)
        {
            var preview = value.Length > 120 ? value[..120] + "..." : value;
            sb.AppendLine($"- {key}: {preview}");
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Delete a key from a subagent's whiteboard. " +
                 "Use the task_id from spawn_subagent as the board_id.")]
    public async Task<string> WhiteboardDelete(
        [Description("The task_id of the subagent (from spawn_subagent)")] string board_id,
        [Description("The key to delete")] string key)
    {
        _logger.LogInformation("PrimaryWhiteboardDelete(board={BoardId}, key={Key})", board_id, key);
        await _whiteboard.DeleteAsync(board_id, key);
        return $"Deleted whiteboard board '{board_id}', key '{key}'.";
    }
}
