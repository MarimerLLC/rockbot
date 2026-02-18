using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Scripts;
using RockBot.Tools;

namespace RockBot.Scripts.Container;

/// <summary>
/// Registers the <c>execute_python_script</c> tool in <see cref="IToolRegistry"/> at startup
/// so the LLM can invoke script execution directly via the tool registry.
/// </summary>
internal sealed class ScriptToolRegistrar(
    IToolRegistry registry,
    IScriptRunner scriptRunner,
    ILogger<ScriptToolRegistrar> logger) : IHostedService
{
    private const string ToolName = "execute_python_script";

    private const string Description =
        "Execute a Python script in a secure ephemeral container. " +
        "Returns the script's stdout on success, or an error message with the exit code. " +
        "Use pip_packages to install dependencies before running.";

    private const string ParametersSchema = """
        {
          "type": "object",
          "properties": {
            "script": {
              "type": "string",
              "description": "Python source code to execute"
            },
            "input_data": {
              "type": "string",
              "description": "Optional data passed as the ROCKBOT_INPUT environment variable"
            },
            "timeout_seconds": {
              "type": "integer",
              "description": "Maximum execution time in seconds (default: 30)"
            },
            "pip_packages": {
              "type": "array",
              "items": { "type": "string" },
              "description": "pip packages to install before running the script (e.g. [\"requests\", \"pandas\"])"
            }
          },
          "required": ["script"]
        }
        """;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var executor = new ScriptToolExecutor(scriptRunner);
        var registration = new ToolRegistration
        {
            Name = ToolName,
            Description = Description,
            ParametersSchema = ParametersSchema,
            Source = "script"
        };

        registry.Register(registration, executor);
        logger.LogInformation("Registered script tool: {ToolName}", ToolName);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
