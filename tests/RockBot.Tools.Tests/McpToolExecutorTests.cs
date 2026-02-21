using System.Text.Json;
using ModelContextProtocol.Protocol;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpToolExecutorTests
{
    [TestMethod]
    public async Task ExecuteAsync_CallsToolAndReturnsContent()
    {
        CallToolDelegate callTool = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "file contents here" }]
        });

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "read_file",
            Arguments = """{"path": "/tmp/test.txt"}"""
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual("call_1", response.ToolCallId);
        Assert.AreEqual("read_file", response.ToolName);
        Assert.AreEqual("file contents here", response.Content);
        Assert.IsFalse(response.IsError);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsIsError_WhenToolReportsError()
    {
        CallToolDelegate callTool = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "something went wrong" }]
        });

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "fail_tool"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("something went wrong", response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_HandlesEmptyContent()
    {
        CallToolDelegate callTool = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
        {
            Content = []
        });

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "no_output"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsNull(response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_HandlesNullArguments()
    {
        Dictionary<string, object?>? capturedArgs = null;
        CallToolDelegate callTool = (args, _) =>
        {
            capturedArgs = args?.ToDictionary(kv => kv.Key, kv => kv.Value);
            return new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "pong" }]
            });
        };

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "ping",
            Arguments = null
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual("pong", response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_ConcatenatesMultipleTextContent()
    {
        CallToolDelegate callTool = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "line 1" },
                new TextContentBlock { Text = "line 2" }
            ]
        });

        var executor = new McpToolExecutor(callTool);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "multi_output"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual("line 1\nline 2", response.Content);
    }

    [TestMethod]
    public void ParseArguments_HandlesNullInput()
    {
        var result = McpToolExecutor.ParseArguments(null);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseArguments_HandlesEmptyString()
    {
        var result = McpToolExecutor.ParseArguments("");
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseArguments_ParsesJsonObject()
    {
        var result = McpToolExecutor.ParseArguments("""{"key": "value", "num": 42}""");
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("value", result["key"]);
        Assert.AreEqual(42L, result["num"]);
    }

    [TestMethod]
    public void ParseArguments_ConvertsArrayOfObjects()
    {
        var json = """
            {
                "provider": "m365",
                "emails": [
                    {"messageId": "msg-001", "folderId": "inbox"},
                    {"messageId": "msg-002", "folderId": "archive"}
                ]
            }
            """;
        var result = McpToolExecutor.ParseArguments(json);

        Assert.AreEqual("m365", result["provider"]);

        var emails = result["emails"] as List<object?>;
        Assert.IsNotNull(emails);
        Assert.AreEqual(2, emails.Count);

        var first = emails[0] as Dictionary<string, object?>;
        Assert.IsNotNull(first);
        Assert.AreEqual("msg-001", first["messageId"]);
        Assert.AreEqual("inbox", first["folderId"]);
    }

    [TestMethod]
    public void ConvertJsonElement_HandlesAllValueKinds()
    {
        var json = """{"s":"hello","n":42,"d":3.14,"t":true,"f":false,"nil":null}""";
        var result = McpToolExecutor.ParseArguments(json);

        Assert.AreEqual("hello", result["s"]);
        Assert.IsInstanceOfType<long>(result["n"]);
        Assert.AreEqual(42L, result["n"]);
        Assert.IsInstanceOfType<double>(result["d"]);
        Assert.AreEqual(3.14, result["d"]);
        Assert.AreEqual(true, result["t"]);
        Assert.AreEqual(false, result["f"]);
        Assert.IsNull(result["nil"]);
    }

    [TestMethod]
    public void ConvertJsonElement_HandlesNestedStructures()
    {
        var json = """
            {
                "items": [
                    {"id": "1", "tags": ["a", "b"]},
                    {"id": "2", "active": true}
                ],
                "count": 2
            }
            """;
        var result = McpToolExecutor.ParseArguments(json);

        Assert.AreEqual(2L, result["count"]);

        var items = result["items"] as List<object?>;
        Assert.IsNotNull(items);
        Assert.AreEqual(2, items.Count);

        var first = items[0] as Dictionary<string, object?>;
        Assert.IsNotNull(first);
        var tags = first["tags"] as List<object?>;
        Assert.IsNotNull(tags);
        CollectionAssert.AreEqual(new object[] { "a", "b" }, tags);
    }

    [TestMethod]
    public void FormatResult_ReturnsNull_ForEmptyContent()
    {
        var result = new CallToolResult { Content = [] };
        Assert.IsNull(McpToolExecutor.FormatResult(result));
    }

    [TestMethod]
    public void FormatResult_ExtractsTextContent()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "hello" }]
        };
        Assert.AreEqual("hello", McpToolExecutor.FormatResult(result));
    }
}
