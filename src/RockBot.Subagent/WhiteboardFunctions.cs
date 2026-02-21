using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Subagent;

/// <summary>
/// LLM-callable tools for the cross-session whiteboard scratchpad.
/// Instantiated per-task with boardId baked in.
/// </summary>
internal sealed class WhiteboardFunctions
{
    public IList<AITool> Tools { get; }

    private readonly IWhiteboardMemory _whiteboard;
    private readonly string _boardId;
    private readonly ILogger _logger;

    public WhiteboardFunctions(IWhiteboardMemory whiteboard, string boardId, ILogger logger)
    {
        _whiteboard = whiteboard;
        _boardId = boardId;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(WhiteboardWrite),
            AIFunctionFactory.Create(WhiteboardRead),
            AIFunctionFactory.Create(WhiteboardList),
            AIFunctionFactory.Create(WhiteboardDelete)
        ];
    }

    [Description("Write a key-value pair to the shared whiteboard. " +
                 "Use the whiteboard to share data between your session and subagent sessions. " +
                 "Choose a descriptive key that summarises what is stored.")]
    public async Task<string> WhiteboardWrite(
        [Description("Short descriptive key (e.g. 'search-results', 'summary')")] string key,
        [Description("The data to store")] string value)
    {
        _logger.LogInformation("WhiteboardWrite(board={BoardId}, key={Key})", _boardId, key);
        await _whiteboard.WriteAsync(_boardId, key, value);
        return $"Wrote to whiteboard key '{key}'.";
    }

    [Description("Read a value from the shared whiteboard by key.")]
    public async Task<string> WhiteboardRead(
        [Description("The key to read")] string key)
    {
        _logger.LogInformation("WhiteboardRead(board={BoardId}, key={Key})", _boardId, key);
        var value = await _whiteboard.ReadAsync(_boardId, key);
        return value ?? $"No whiteboard entry found for key '{key}'.";
    }

    [Description("List all keys and values on the shared whiteboard.")]
    public async Task<string> WhiteboardList()
    {
        _logger.LogInformation("WhiteboardList(board={BoardId})", _boardId);
        var entries = await _whiteboard.ListAsync(_boardId);

        if (entries.Count == 0)
            return "Whiteboard is empty.";

        var sb = new StringBuilder();
        sb.AppendLine($"Whiteboard ({entries.Count} entries):");
        foreach (var (key, value) in entries)
        {
            var preview = value.Length > 120 ? value[..120] + "..." : value;
            sb.AppendLine($"- {key}: {preview}");
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Delete a key from the shared whiteboard.")]
    public async Task<string> WhiteboardDelete(
        [Description("The key to delete")] string key)
    {
        _logger.LogInformation("WhiteboardDelete(board={BoardId}, key={Key})", _boardId, key);
        await _whiteboard.DeleteAsync(_boardId, key);
        return $"Deleted whiteboard key '{key}'.";
    }
}
