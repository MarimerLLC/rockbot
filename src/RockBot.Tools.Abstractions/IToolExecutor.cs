namespace RockBot.Tools;

/// <summary>
/// Executes a tool invocation and returns the result.
/// </summary>
public interface IToolExecutor
{
    /// <summary>
    /// Execute the tool with the given request.
    /// </summary>
    Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct);
}
