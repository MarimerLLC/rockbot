namespace RockBot.Tools;

/// <summary>
/// Registry of available tools and their executors.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Returns all registered tool definitions.
    /// </summary>
    IReadOnlyList<ToolRegistration> GetTools();

    /// <summary>
    /// Returns the executor for the named tool, or null if not registered.
    /// </summary>
    IToolExecutor? GetExecutor(string toolName);

    /// <summary>
    /// Register a tool with its executor.
    /// </summary>
    void Register(ToolRegistration registration, IToolExecutor executor);

    /// <summary>
    /// Removes a tool by name. Returns true if the tool was found and removed.
    /// </summary>
    bool Unregister(string toolName);
}
