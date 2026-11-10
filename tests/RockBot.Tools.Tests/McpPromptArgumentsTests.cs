using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpPromptArgumentsTests
{
    [TestMethod]
    public void ToPromptArguments_Object_StringsAsIs_OthersAsRawJson()
    {
        var result = McpManagementExecutor.ToPromptArguments("""{"topic":"cats","count":3,"tags":["a","b"]}""");

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual("cats", result["topic"]);
        Assert.AreEqual("3", result["count"]);
        Assert.AreEqual("""["a","b"]""", result["tags"]);
    }

    [TestMethod]
    public void ToPromptArguments_StringifiedObject_IsReadThroughTheQuoting()
    {
        // #782: `arguments` sent as a JSON string holding the object.
        var result = McpManagementExecutor.ToPromptArguments("""
            "{\"topic\":\"cats\",\"count\":3}"
            """);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("cats", result["topic"]);
        Assert.AreEqual("3", result["count"]);
    }

    [TestMethod]
    public void ToPromptArguments_MalformedOrNonObject_YieldsNoArguments()
    {
        Assert.AreEqual(0, McpManagementExecutor.ToPromptArguments(null).Count);
        Assert.AreEqual(0, McpManagementExecutor.ToPromptArguments("not json").Count);
        Assert.AreEqual(0, McpManagementExecutor.ToPromptArguments("\"plain text\"").Count);
        Assert.AreEqual(0, McpManagementExecutor.ToPromptArguments("[1,2]").Count);
    }
}
