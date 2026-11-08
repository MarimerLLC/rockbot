using System.Text;
using System.Text.Json.Nodes;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the body-rewrite helper. Wire-level registration (OpenRouter + Claude gating) is
/// exercised where <see cref="PromptCachingPolicy"/> is wired up in <c>Program.cs</c>.
/// </summary>
[TestClass]
public sealed class PromptCachingPolicyTests
{
    private static string? Inject(string body) =>
        PromptCachingPolicy.InjectInto(Encoding.UTF8.GetBytes(body));

    [TestMethod]
    public void InjectInto_ConvertsStringSystemContent_ToCacheableArrayForm()
    {
        const string body = """
            {"model":"anthropic/claude-sonnet-5.5","messages":[
              {"role":"system","content":"you are an agent"},
              {"role":"user","content":"hi"}
            ]}
            """;

        var json = (JsonObject)JsonNode.Parse(Inject(body)!)!;
        var messages = json["messages"]!.AsArray();
        var systemContent = messages[0]!["content"]!.AsArray();

        Assert.AreEqual(1, systemContent.Count);
        Assert.AreEqual("text", (string)systemContent[0]!["type"]!);
        Assert.AreEqual("you are an agent", (string)systemContent[0]!["text"]!);
        Assert.AreEqual("ephemeral", (string)systemContent[0]!["cache_control"]!["type"]!);
    }

    [TestMethod]
    public void InjectInto_TagsSecondToLastMessage_NotTheNewestOne()
    {
        const string body = """
            {"messages":[
              {"role":"system","content":"sys"},
              {"role":"user","content":"first"},
              {"role":"assistant","content":"reply"},
              {"role":"user","content":"newest - must stay untagged"}
            ]}
            """;

        var json = (JsonObject)JsonNode.Parse(Inject(body)!)!;
        var messages = json["messages"]!.AsArray();

        // index 2 ("reply") is second-to-last — gets the incremental breakpoint.
        var tagged = messages[2]!["content"]!.AsArray();
        Assert.AreEqual("ephemeral", (string)tagged[0]!["cache_control"]!["type"]!);

        // The newest message (last) must never be tagged — it changes every turn, so caching
        // it would never hit and would just add a wasted write.
        Assert.IsTrue(messages[3]!["content"] is JsonValue, "newest message must stay a plain string");
    }

    [TestMethod]
    public void InjectInto_DoesNotDoubleTag_AnAlreadyCacheableBlock()
    {
        const string body = """
            {"messages":[
              {"role":"system","content":[{"type":"text","text":"sys","cache_control":{"type":"ephemeral"}}]},
              {"role":"user","content":"hi"}
            ]}
            """;

        // Re-running on an already-tagged body must be a no-op: nothing left to change.
        Assert.IsNull(Inject(body));
    }

    [TestMethod]
    public void InjectInto_HandlesSingleMessage_SystemBreakpointOnly()
    {
        const string body = """{"messages":[{"role":"system","content":"sys only"}]}""";

        var json = (JsonObject)JsonNode.Parse(Inject(body)!)!;
        var systemContent = json["messages"]![0]!["content"]!.AsArray();

        Assert.AreEqual("ephemeral", (string)systemContent[0]!["cache_control"]!["type"]!);
    }

    [TestMethod]
    public void InjectInto_ReturnsNull_WhenNoSystemMessageAndOnlyOneMessage()
    {
        const string body = """{"messages":[{"role":"user","content":"hi"}]}""";

        Assert.IsNull(Inject(body));
    }

    [TestMethod]
    public void InjectInto_ReturnsNull_ForNonChatCompletionBody()
    {
        Assert.IsNull(Inject("""{"model":"nomic-embed-text","input":"hi"}"""));
    }

    [TestMethod]
    public void InjectInto_ReturnsNull_ForUnparseableOrNonObjectBody()
    {
        Assert.IsNull(Inject("not json at all"));
        Assert.IsNull(Inject("[1,2,3]"));
    }

    [TestMethod]
    public void InjectInto_PreservesExistingBodyFields()
    {
        const string body = """
            {"model":"anthropic/claude-sonnet-5.5","temperature":0.7,"messages":[
              {"role":"system","content":"sys"},
              {"role":"user","content":"hi"}
            ]}
            """;

        var json = (JsonObject)JsonNode.Parse(Inject(body)!)!;

        Assert.AreEqual("anthropic/claude-sonnet-5.5", (string)json["model"]!);
        Assert.AreEqual(0.7, (double)json["temperature"]!);
    }

    [TestMethod]
    [DataRow("anthropic/claude-sonnet-5.5", true)]
    [DataRow("anthropic/claude-haiku-5.5", true)]
    [DataRow("claude-opus-4.6", true)]
    [DataRow("CLAUDE-SONNET-5.5", true)]
    [DataRow("x-ai/grok-4.6", false)]
    [DataRow("gpt-5.4", false)]
    [DataRow(null, false)]
    public void IsClaudeModel_MatchesClaudeIdsCaseInsensitively(string? modelId, bool expected)
        => Assert.AreEqual(expected, PromptCachingPolicy.IsClaudeModel(modelId));
}
