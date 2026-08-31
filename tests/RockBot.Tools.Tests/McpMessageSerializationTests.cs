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
}
