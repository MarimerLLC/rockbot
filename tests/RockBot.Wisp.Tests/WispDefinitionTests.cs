using System.Text.Json;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

[TestClass]
public class WispDefinitionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void Deserialize_SimpleDataPipeline_RoundTrips()
    {
        var json = """
        {
          "description": "Download and parse a file",
          "steps": [
            {
              "id": "download",
              "gateway": "Mcp",
              "mode": "Direct",
              "server": "onedrive",
              "tool": "download_file",
              "params": { "path": "/reports/Q1.xlsx" }
            },
            {
              "id": "summarize",
              "mode": "Llm",
              "prompt": "Summarize the data"
            }
          ]
        }
        """;

        var definition = JsonSerializer.Deserialize<WispDefinition>(json, JsonOptions)!;

        Assert.AreEqual("Download and parse a file", definition.Description);
        Assert.AreEqual(2, definition.Steps.Count);
        Assert.IsNull(definition.Tools);

        var step0 = definition.Steps[0];
        Assert.AreEqual("download", step0.Id);
        Assert.AreEqual(GatewayType.Mcp, step0.Gateway);
        Assert.AreEqual(StepMode.Direct, step0.Mode);
        Assert.AreEqual("onedrive", step0.Server);
        Assert.AreEqual("download_file", step0.Tool);
        Assert.IsNotNull(step0.Params);

        var step1 = definition.Steps[1];
        Assert.AreEqual("summarize", step1.Id);
        Assert.AreEqual(StepMode.Llm, step1.Mode);
        Assert.IsNull(step1.Gateway);
        Assert.AreEqual("Summarize the data", step1.Prompt);
    }

    [TestMethod]
    public void Deserialize_WithToolsArray_ParsesCorrectly()
    {
        var json = """
        {
          "description": "Research task",
          "tools": ["web_browse", "web_search"],
          "steps": [
            {
              "id": "search",
              "gateway": "Web",
              "tool": "web_search",
              "mode": "Direct",
              "params": { "query": "test", "count": 5 }
            }
          ]
        }
        """;

        var definition = JsonSerializer.Deserialize<WispDefinition>(json, JsonOptions)!;

        Assert.IsNotNull(definition.Tools);
        Assert.AreEqual(2, definition.Tools.Count);
        Assert.AreEqual("web_browse", definition.Tools[0]);
        Assert.AreEqual("web_search", definition.Tools[1]);
    }

    [TestMethod]
    public void Deserialize_OnFailure_ParsesCorrectly()
    {
        var json = """
        {
          "description": "With failure handling",
          "steps": [
            {
              "id": "risky",
              "gateway": "Mcp",
              "mode": "Direct",
              "server": "test",
              "tool": "test_tool",
              "on_failure": { "action": "skip_to", "skip_to": "fallback" }
            },
            {
              "id": "fallback",
              "gateway": "Web",
              "tool": "web_search",
              "mode": "Direct",
              "params": { "query": "fallback" }
            }
          ]
        }
        """;

        var definition = JsonSerializer.Deserialize<WispDefinition>(json, JsonOptions)!;

        var step0 = definition.Steps[0];
        Assert.IsNotNull(step0.OnFailure);
        Assert.AreEqual("skip_to", step0.OnFailure.Action);
        Assert.AreEqual("fallback", step0.OnFailure.SkipTo);
    }

    [TestMethod]
    public void Deserialize_ScriptStep_ParsesLanguageAndParams()
    {
        var json = """
        {
          "description": "Script execution",
          "steps": [
            {
              "id": "run_script",
              "gateway": "Script",
              "mode": "Direct",
              "language": "python",
              "params": {
                "script": "print('hello')",
                "pip_packages": ["pandas"],
                "timeout_seconds": 60
              },
              "output_to": "/shared/wisp-abc/result.json"
            }
          ]
        }
        """;

        var definition = JsonSerializer.Deserialize<WispDefinition>(json, JsonOptions)!;

        var step = definition.Steps[0];
        Assert.AreEqual("python", step.Language);
        Assert.AreEqual("/shared/wisp-abc/result.json", step.OutputTo);
        Assert.IsNotNull(step.Params);
    }

    [TestMethod]
    public void Deserialize_A2AStep_ParsesAgentFields()
    {
        var json = """
        {
          "description": "Agent invocation",
          "steps": [
            {
              "id": "analyze",
              "gateway": "A2A",
              "mode": "Direct",
              "agent": "market-analyst",
              "skill": "competitive-analysis",
              "message": "Analyze the data",
              "timeout_minutes": 10
            }
          ]
        }
        """;

        var definition = JsonSerializer.Deserialize<WispDefinition>(json, JsonOptions)!;

        var step = definition.Steps[0];
        Assert.AreEqual(GatewayType.A2A, step.Gateway);
        Assert.AreEqual("market-analyst", step.Agent);
        Assert.AreEqual("competitive-analysis", step.Skill);
        Assert.AreEqual("Analyze the data", step.Message);
        Assert.AreEqual(10, step.TimeoutMinutes);
    }

    [TestMethod]
    public void Deserialize_LlmStep_WithInputFrom()
    {
        var json = """
        {
          "description": "LLM with input",
          "steps": [
            {
              "id": "summarize",
              "mode": "Llm",
              "prompt": "Summarize trends",
              "input_from": "/shared/wisp-abc/parsed.json",
              "output_to": "/shared/wisp-abc/summary.txt"
            }
          ]
        }
        """;

        var definition = JsonSerializer.Deserialize<WispDefinition>(json, JsonOptions)!;

        var step = definition.Steps[0];
        Assert.AreEqual(StepMode.Llm, step.Mode);
        Assert.AreEqual("/shared/wisp-abc/parsed.json", step.InputFrom);
        Assert.AreEqual("/shared/wisp-abc/summary.txt", step.OutputTo);
    }
}
