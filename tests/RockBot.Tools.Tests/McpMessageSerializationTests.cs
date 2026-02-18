using RockBot.Messaging;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpMessageSerializationTests
{
    [TestMethod]
    public void McpToolDefinition_RoundTrips()
    {
        var original = new McpToolDefinition
        {
            Name = "read_file",
            Description = "Reads a file from disk",
            ParametersSchema = """{"type":"object","properties":{"path":{"type":"string"}}}"""
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpToolDefinition>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(original.Name, deserialized.Name);
        Assert.AreEqual(original.Description, deserialized.Description);
        Assert.AreEqual(original.ParametersSchema, deserialized.ParametersSchema);
    }

    [TestMethod]
    public void McpToolDefinition_NullSchema_RoundTrips()
    {
        var original = new McpToolDefinition
        {
            Name = "no_args_tool",
            Description = "A tool with no parameters",
            ParametersSchema = null
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpToolDefinition>();

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.ParametersSchema);
    }

    [TestMethod]
    public void McpToolsAvailable_RoundTrips()
    {
        var original = new McpToolsAvailable
        {
            ServerName = "filesystem",
            Tools =
            [
                new McpToolDefinition
                {
                    Name = "read_file",
                    Description = "Reads a file",
                    ParametersSchema = """{"type":"object"}"""
                },
                new McpToolDefinition
                {
                    Name = "write_file",
                    Description = "Writes a file"
                }
            ],
            RemovedTools = ["old_tool", "deprecated_tool"]
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpToolsAvailable>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("filesystem", deserialized.ServerName);
        Assert.AreEqual(2, deserialized.Tools.Count);
        Assert.AreEqual("read_file", deserialized.Tools[0].Name);
        Assert.AreEqual("write_file", deserialized.Tools[1].Name);
        Assert.AreEqual(2, deserialized.RemovedTools.Count);
        Assert.AreEqual("old_tool", deserialized.RemovedTools[0]);
    }

    [TestMethod]
    public void McpToolsAvailable_EmptyLists_RoundTrips()
    {
        var original = new McpToolsAvailable
        {
            ServerName = "empty-server",
            Tools = [],
            RemovedTools = []
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpToolsAvailable>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(0, deserialized.Tools.Count);
        Assert.AreEqual(0, deserialized.RemovedTools.Count);
    }

    [TestMethod]
    public void McpMetadataRefreshRequest_WithServerName_RoundTrips()
    {
        var original = new McpMetadataRefreshRequest
        {
            ServerName = "filesystem"
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpMetadataRefreshRequest>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("filesystem", deserialized.ServerName);
    }

    [TestMethod]
    public void McpMetadataRefreshRequest_NullServerName_RoundTrips()
    {
        var original = new McpMetadataRefreshRequest
        {
            ServerName = null
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpMetadataRefreshRequest>();

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.ServerName);
    }

    [TestMethod]
    public void McpToolsAvailable_MessageType_IsClrTypeName()
    {
        var message = new McpToolsAvailable
        {
            ServerName = "test",
            Tools = [],
            RemovedTools = []
        };

        var envelope = message.ToEnvelope("test");

        Assert.AreEqual(typeof(McpToolsAvailable).FullName, envelope.MessageType);
    }
}
