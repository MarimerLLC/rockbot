using RockBot.Messaging;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpManagementMessageSerializationTests
{
    // ── McpServersIndexed ────────────────────────────────────────────────────

    [TestMethod]
    public void McpServersIndexed_RoundTrips()
    {
        var original = new McpServersIndexed
        {
            Servers =
            [
                new McpServerSummary
                {
                    ServerName = "filesystem",
                    DisplayName = "File System",
                    Summary = "Provides file system access tools.",
                    ToolCount = 3,
                    ToolNames = ["read_file", "write_file", "list_dir"]
                }
            ],
            RemovedServers = ["old-server"]
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpServersIndexed>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(1, deserialized.Servers.Count);
        Assert.AreEqual("filesystem", deserialized.Servers[0].ServerName);
        Assert.AreEqual("File System", deserialized.Servers[0].DisplayName);
        Assert.AreEqual(3, deserialized.Servers[0].ToolCount);
        Assert.AreEqual(3, deserialized.Servers[0].ToolNames.Count);
        Assert.AreEqual(1, deserialized.RemovedServers.Count);
        Assert.AreEqual("old-server", deserialized.RemovedServers[0]);
    }

    [TestMethod]
    public void McpServersIndexed_EmptyLists_RoundTrips()
    {
        var original = new McpServersIndexed
        {
            Servers = [],
            RemovedServers = []
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpServersIndexed>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(0, deserialized.Servers.Count);
        Assert.AreEqual(0, deserialized.RemovedServers.Count);
    }

    [TestMethod]
    public void McpServersIndexed_MessageType_IsClrTypeName()
    {
        var message = new McpServersIndexed { Servers = [] };
        var envelope = message.ToEnvelope("test");
        Assert.AreEqual(typeof(McpServersIndexed).FullName, envelope.MessageType);
    }

    // ── McpGetServiceDetails ─────────────────────────────────────────────────

    [TestMethod]
    public void McpGetServiceDetailsRequest_RoundTrips()
    {
        var original = new McpGetServiceDetailsRequest { ServerName = "filesystem" };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpGetServiceDetailsRequest>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("filesystem", deserialized.ServerName);
    }

    [TestMethod]
    public void McpGetServiceDetailsResponse_RoundTrips()
    {
        var original = new McpGetServiceDetailsResponse
        {
            ServerName = "filesystem",
            Tools =
            [
                new McpToolDefinition
                {
                    Name = "read_file",
                    Description = "Reads a file",
                    ParametersSchema = """{"type":"object"}"""
                }
            ]
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpGetServiceDetailsResponse>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("filesystem", deserialized.ServerName);
        Assert.AreEqual(1, deserialized.Tools.Count);
        Assert.AreEqual("read_file", deserialized.Tools[0].Name);
        Assert.IsNull(deserialized.Error);
    }

    [TestMethod]
    public void McpGetServiceDetailsResponse_WithError_RoundTrips()
    {
        var original = new McpGetServiceDetailsResponse
        {
            ServerName = "missing",
            Error = "Server not connected"
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpGetServiceDetailsResponse>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("Server not connected", deserialized.Error);
        Assert.AreEqual(0, deserialized.Tools.Count);
    }

    // ── McpRegisterServer ────────────────────────────────────────────────────

    [TestMethod]
    public void McpRegisterServerRequest_RoundTrips()
    {
        var original = new McpRegisterServerRequest
        {
            ServerName = "my-server",
            Type = "sse",
            Url = "http://localhost:3000/sse"
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpRegisterServerRequest>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("my-server", deserialized.ServerName);
        Assert.AreEqual("sse", deserialized.Type);
        Assert.AreEqual("http://localhost:3000/sse", deserialized.Url);
    }

    [TestMethod]
    public void McpRegisterServerResponse_Success_RoundTrips()
    {
        var original = new McpRegisterServerResponse
        {
            ServerName = "my-server",
            Success = true,
            Summary = "Provides 5 tools."
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpRegisterServerResponse>();

        Assert.IsNotNull(deserialized);
        Assert.IsTrue(deserialized.Success);
        Assert.AreEqual("Provides 5 tools.", deserialized.Summary);
        Assert.IsNull(deserialized.Error);
    }

    [TestMethod]
    public void McpRegisterServerResponse_Failure_RoundTrips()
    {
        var original = new McpRegisterServerResponse
        {
            ServerName = "my-server",
            Success = false,
            Error = "Connection refused"
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpRegisterServerResponse>();

        Assert.IsNotNull(deserialized);
        Assert.IsFalse(deserialized.Success);
        Assert.AreEqual("Connection refused", deserialized.Error);
    }

    // ── McpUnregisterServer ──────────────────────────────────────────────────

    [TestMethod]
    public void McpUnregisterServerRequest_RoundTrips()
    {
        var original = new McpUnregisterServerRequest { ServerName = "filesystem" };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpUnregisterServerRequest>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("filesystem", deserialized.ServerName);
    }

    [TestMethod]
    public void McpUnregisterServerResponse_RoundTrips()
    {
        var original = new McpUnregisterServerResponse
        {
            ServerName = "filesystem",
            Success = true
        };

        var envelope = original.ToEnvelope("test");
        var deserialized = envelope.GetPayload<McpUnregisterServerResponse>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("filesystem", deserialized.ServerName);
        Assert.IsTrue(deserialized.Success);
        Assert.IsNull(deserialized.Error);
    }
}
