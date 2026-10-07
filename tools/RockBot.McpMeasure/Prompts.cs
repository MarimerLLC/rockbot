namespace RockBot.McpMeasure;

/// <summary>The system prompts a run can use.</summary>
public static class Prompts
{
    /// <summary>
    /// The primary agent's profile documents in its composition order (soul, safety rules, common
    /// directives, directives, memory rules), from the agent/ folder copied with the agent project.
    /// The live agent's PVC copies can differ; these are the repo's.
    /// </summary>
    public static string Agent(string baseDirectory)
    {
        var dir = Path.Combine(baseDirectory, "agent");
        string[] files = ["soul.md", "safety-rules.md", "common-directives.md", "directives.md", "memory-rules.md"];
        var parts = files.Select(f => Path.Combine(dir, f)).Where(File.Exists).Select(File.ReadAllText).ToList();
        if (parts.Count == 0)
            throw new InvalidOperationException($"No agent profile documents in {dir}.");
        return string.Join("\n\n---\n\n", parts);
    }

    /// <summary>mcp-aggregator#42's prompt: complete the request with the tools, assume rather than ask.</summary>
    public static string Minimal() =>
        "You are an assistant that completes the user's request by calling the tools available to you. " +
        "Make reasonable assumptions instead of asking clarifying questions. Call the tool that carries out the " +
        "request; when it has been carried out, reply with one short sentence confirming what was done.";
}
