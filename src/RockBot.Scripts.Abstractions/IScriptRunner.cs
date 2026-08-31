namespace RockBot.Scripts;

/// <summary>
/// Executes scripts in isolated containers and returns the result.
/// </summary>
public interface IScriptRunner
{
    Task<ScriptInvokeResponse> ExecuteAsync(ScriptInvokeRequest request, CancellationToken ct);
}
