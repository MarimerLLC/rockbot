using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpManagementExecutorTests
{
    private readonly AgentIdentity _identity = new("test-agent");

    private (McpManagementExecutor Executor, TrackingPublisher Publisher, StubSubscriber Subscriber)
        CreateExecutor(TimeSpan? timeout = null)
    {
        var publisher = new TrackingPublisher();
        var subscriber = new StubSubscriber();

        var proxy = new McpToolProxy(
            publisher,
            subscriber,
            _identity,
            NullLogger<McpToolProxy>.Instance);

        var index = new McpServerIndex();
        index.Apply(new McpServersIndexed
        {
            Servers =
            [
                new McpServerSummary
                {
                    ServerName = "filesystem",
                    Summary = "File system tools.",
                    ToolCount = 2,
                    ToolNames = ["read_file", "write_file"]
                }
            ]
        });

        var executor = new McpManagementExecutor(
            index,
            proxy,
            publisher,
            subscriber,
            _identity,
            NullLogger<McpManagementExecutor>.Instance,
            timeout);

        return (executor, publisher, subscriber);
    }

    // ── mcp_list_services ────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListServices_ReturnsCachedServerJson()
    {
        var (executor, publisher, _) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_list_services"
        };

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.IsNotNull(result.Content);

        // Should contain filesystem server
        Assert.IsTrue(result.Content.Contains("filesystem"));

        // Should NOT have published anything (no bridge round-trip)
        Assert.AreEqual(0, publisher.Published.Count);
    }

    [TestMethod]
    public async Task ListServices_ReturnsValidJson()
    {
        var (executor, _, _) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_list_services"
        };

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        var servers = JsonSerializer.Deserialize<List<object>>(result.Content!);
        Assert.IsNotNull(servers);
        Assert.AreEqual(1, servers.Count);
    }

    // ── mcp_get_service_details ──────────────────────────────────────────────

    [TestMethod]
    public async Task GetServiceDetails_PublishesToManageTopic()
    {
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_get_service_details",
            Arguments = """{"server_name":"filesystem"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count);
        Assert.AreEqual(McpManagementExecutor.ManageTopic, publisher.Published[0].Topic);

        var published = publisher.Published[0].Envelope;
        Assert.AreEqual(typeof(McpGetServiceDetailsRequest).FullName, published.MessageType);
        Assert.AreEqual(executor.ResponseTopic, published.ReplyTo);

        // Simulate bridge response
        var response = new McpGetServiceDetailsResponse
        {
            ServerName = "filesystem",
            Tools =
            [
                new McpToolDefinition { Name = "read_file", Description = "Reads a file" }
            ]
        };
        var responseEnvelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync(executor.ResponseTopic, responseEnvelope);

        var result = await executeTask;
        Assert.IsFalse(result.IsError);
        Assert.IsTrue(result.Content!.Contains("read_file"));
    }

    [TestMethod]
    public async Task GetServiceDetails_SurfacesServerIdentityToAgent()
    {
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_get_service_details",
            Arguments = """{"server_name":"uber"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        var published = publisher.Published[0].Envelope;
        var response = new McpGetServiceDetailsResponse
        {
            ServerName = "uber",
            ImplementationName = "rides-3p-demand-mcp",
            Title = "Uber",
            Version = "1.2.3",
            Instructions = "MCP server for rides-3p-demand-mcp service",
            Tools =
            [
                new McpToolDefinition { Name = "get_estimates_between_two_locations", Description = "..." }
            ]
        };
        var responseEnvelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync(executor.ResponseTopic, responseEnvelope);

        var result = await executeTask;

        Assert.IsFalse(result.IsError);
        var doc = JsonDocument.Parse(result.Content!);
        var server = doc.RootElement.GetProperty("server");
        Assert.AreEqual("uber", server.GetProperty("name").GetString());
        Assert.AreEqual("rides-3p-demand-mcp", server.GetProperty("implementationName").GetString());
        Assert.AreEqual("1.2.3", server.GetProperty("version").GetString());
        Assert.AreEqual("MCP server for rides-3p-demand-mcp service",
            server.GetProperty("instructions").GetString());
        Assert.AreEqual(1, doc.RootElement.GetProperty("tools").GetArrayLength());
    }

    [TestMethod]
    public async Task GetServiceDetails_MissingServerName_ReturnsError()
    {
        var (executor, _, _) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_get_service_details",
            Arguments = "{}"
        };

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.Content!.Contains("server_name"));
    }

    [TestMethod]
    public async Task GetServiceDetails_Timeout_ReturnsError()
    {
        var (executor, _, _) = CreateExecutor(timeout: TimeSpan.FromMilliseconds(100));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_get_service_details",
            Arguments = """{"server_name":"filesystem"}"""
        };

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.Content!.Contains("Timed out"));
    }

    // ── mcp_invoke_tool ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task InvokeTool_DelegatesToProxyWithServerHeader()
    {
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_invoke_tool",
            Arguments = """{"server_name":"filesystem","tool_name":"read_file","arguments":{"path":"/tmp/test.txt"}}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        // Should have published to tool.invoke.mcp
        Assert.AreEqual(1, publisher.Published.Count);
        Assert.AreEqual(McpToolProxy.InvokeTopic, publisher.Published[0].Topic);

        var published = publisher.Published[0].Envelope;
        var innerRequest = published.GetPayload<ToolInvokeRequest>();
        Assert.IsNotNull(innerRequest);
        Assert.AreEqual("read_file", innerRequest.ToolName);

        // Should have the rb-mcp-server header
        Assert.IsTrue(published.Headers.ContainsKey(McpHeaders.ServerName));
        Assert.AreEqual("filesystem", published.Headers[McpHeaders.ServerName]);

        // Simulate response
        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-1",
            ToolName = "read_file",
            Content = "file contents"
        };
        var responseEnvelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync($"tool.result.{_identity.Name}", responseEnvelope);

        var result = await executeTask;
        Assert.IsFalse(result.IsError);
        Assert.AreEqual("file contents", result.Content);
    }

    [TestMethod]
    [DataRow("arguments")]
    [DataRow("params")]
    [DataRow("args")]
    public async Task InvokeTool_AcceptsNestedArgAliases(string nestedKey)
    {
        // Some models (notably gpt-5.4) emit the nested payload under "params" or "args"
        // instead of the schema-declared "arguments". All three should be forwarded.
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-alias",
            ToolName = "mcp_invoke_tool",
            Arguments = "{\"server_name\":\"filesystem\",\"tool_name\":\"read_file\",\"" + nestedKey + "\":{\"path\":\"/tmp/x.txt\"}}"
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count);
        var innerRequest = publisher.Published[0].Envelope.GetPayload<ToolInvokeRequest>();
        Assert.IsNotNull(innerRequest);
        Assert.IsNotNull(innerRequest.Arguments, $"nested payload under '{nestedKey}' was dropped");
        StringAssert.Contains(innerRequest.Arguments, "/tmp/x.txt");

        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-alias",
            ToolName = "read_file",
            Content = "ok"
        };
        var responseEnvelope = response.ToEnvelope("bridge",
            correlationId: publisher.Published[0].Envelope.CorrelationId);
        await subscriber.DeliverAsync($"tool.result.{_identity.Name}", responseEnvelope);
        await executeTask;
    }

    [TestMethod]
    public async Task InvokeTool_FlatTopLevelFields_PromotedIntoArguments()
    {
        // Some models drop the `arguments` wrapper and inline the inner-tool fields
        // directly at the top level of mcp_invoke_tool (e.g. `accountId`, `query`,
        // `count` sitting next to `server_name`/`tool_name`). Without promotion the
        // call dispatches with no inner args and the MCP server rejects with
        // "X is required" — even though the LLM did pass X, one level up.
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-flat",
            ToolName = "mcp_invoke_tool",
            Arguments = """
                {"server_name":"calendar-mcp","tool_name":"search_emails",
                 "accountId":"rockbotagent","query":"VS Live Co-Chair","count":10}
                """
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count, "Flat-args call must dispatch.");
        var innerRequest = publisher.Published[0].Envelope.GetPayload<ToolInvokeRequest>();
        Assert.IsNotNull(innerRequest);
        Assert.IsNotNull(innerRequest.Arguments,
            "Flat top-level fields must be promoted into 'arguments', not dropped.");
        StringAssert.Contains(innerRequest.Arguments, "rockbotagent");
        StringAssert.Contains(innerRequest.Arguments, "VS Live Co-Chair");
        StringAssert.Contains(innerRequest.Arguments, "count");

        // The wrapper keys must NOT have leaked into the inner args
        Assert.IsFalse(innerRequest.Arguments.Contains("server_name"),
            "Reserved wrapper key 'server_name' must not leak into inner tool args.");
        Assert.IsFalse(innerRequest.Arguments.Contains("tool_name"),
            "Reserved wrapper key 'tool_name' must not leak into inner tool args.");

        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-flat",
            ToolName = "search_emails",
            Content = "[]"
        };
        await subscriber.DeliverAsync($"tool.result.{_identity.Name}",
            response.ToEnvelope("bridge", correlationId: publisher.Published[0].Envelope.CorrelationId));
        await executeTask;
    }

    [TestMethod]
    public async Task InvokeTool_NestedArgsTakePrecedenceOverFlatFields()
    {
        // When both shapes are present, the explicit `arguments` wrapper wins —
        // anything stray at the top level is ignored (treating it as model noise
        // is safer than mixing both into the inner call).
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-both",
            ToolName = "mcp_invoke_tool",
            Arguments = """
                {"server_name":"calendar-mcp","tool_name":"search_emails",
                 "arguments":{"accountId":"good","query":"good"},
                 "stray":"ignored"}
                """
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count);
        var innerRequest = publisher.Published[0].Envelope.GetPayload<ToolInvokeRequest>();
        Assert.IsNotNull(innerRequest);
        Assert.IsNotNull(innerRequest.Arguments);
        StringAssert.Contains(innerRequest.Arguments, "good");
        Assert.IsFalse(innerRequest.Arguments.Contains("stray"),
            "Stray top-level field must not be merged when 'arguments' wrapper is present.");

        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-both",
            ToolName = "search_emails",
            Content = "[]"
        };
        await subscriber.DeliverAsync($"tool.result.{_identity.Name}",
            response.ToEnvelope("bridge", correlationId: publisher.Published[0].Envelope.CorrelationId));
        await executeTask;
    }

    [TestMethod]
    public async Task InvokeTool_MissingServerName_ReturnsError()
    {
        var (executor, _, _) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_invoke_tool",
            Arguments = """{"tool_name":"read_file"}"""
        };

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.Content!.Contains("server_name"));
    }

    // ── mcp_invoke_tool: empty-args pre-flight remediation ───────────────────

    private (McpManagementExecutor Executor, TrackingPublisher Publisher, StubSubscriber Subscriber)
        CreateExecutorWithSchemas(IReadOnlyList<McpToolDefinition> tools)
    {
        var publisher = new TrackingPublisher();
        var subscriber = new StubSubscriber();

        var proxy = new McpToolProxy(
            publisher,
            subscriber,
            _identity,
            NullLogger<McpToolProxy>.Instance);

        var index = new McpServerIndex();
        index.Apply(new McpServersIndexed
        {
            Servers =
            [
                new McpServerSummary
                {
                    ServerName = "filesystem",
                    Summary = "File system tools.",
                    ToolCount = tools.Count,
                    ToolNames = tools.Select(t => t.Name).ToList()
                }
            ]
        });

        var schemaCache = new ToolSchemaCache((server, ct) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>?>(
                string.Equals(server, "filesystem", StringComparison.OrdinalIgnoreCase) ? tools : null));

        var executor = new McpManagementExecutor(
            index,
            proxy,
            publisher,
            subscriber,
            _identity,
            NullLogger<McpManagementExecutor>.Instance,
            timeout: null,
            recovery: null,
            skillStore: null,
            schemaCache: schemaCache);

        return (executor, publisher, subscriber);
    }

    [TestMethod]
    public async Task InvokeTool_EmptyArgs_RequiredParamsTool_ReturnsRemediationBeforeDispatch()
    {
        // Repro of the patrol-trace failure: LLM emits a mcp_invoke_tool call with
        // only server_name + tool_name for a parameterized tool. The framework should
        // short-circuit with a remediation that names the required fields and shows
        // the wrapper shape, instead of dispatching and letting the MCP server return
        // a tool-server error that the model misinterprets as "the wrapper is broken."
        var schema = """
            {"type":"object","properties":{"to":{"type":"string"},"subject":{"type":"string"},"body":{"type":"string"}},"required":["to","subject","body"]}
            """;
        var (executor, publisher, _) = CreateExecutorWithSchemas(
        [
            new McpToolDefinition { Name = "send_email", Description = "Send email.", ParametersSchema = schema }
        ]);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-empty",
            ToolName = "mcp_invoke_tool",
            Arguments = """{"server_name":"filesystem","tool_name":"send_email"}"""
        };

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsError, "Empty-args call to a parameterized tool must short-circuit.");
        Assert.AreEqual(0, publisher.Published.Count,
            "Pre-flight remediation must not dispatch to the bridge.");
        StringAssert.Contains(result.Content!, "to");
        StringAssert.Contains(result.Content!, "subject");
        StringAssert.Contains(result.Content!, "body");
        StringAssert.Contains(result.Content!, "arguments",
            "Remediation must point the model at the 'arguments' wrapper field.");
    }

    [TestMethod]
    public async Task InvokeTool_EmptyArgs_NoRequiredParams_DispatchesNormally()
    {
        // No-arg MCP tools (e.g. list_accounts) must still dispatch when the wrapper
        // carries only server_name + tool_name — the remediation is gated on the
        // schema declaring required fields.
        var schema = """{"type":"object","properties":{}}""";
        var (executor, publisher, subscriber) = CreateExecutorWithSchemas(
        [
            new McpToolDefinition { Name = "list_accounts", Description = "List accounts.", ParametersSchema = schema }
        ]);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-noargs",
            ToolName = "mcp_invoke_tool",
            Arguments = """{"server_name":"filesystem","tool_name":"list_accounts"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count,
            "No-required-params tool should reach the bridge.");

        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-noargs",
            ToolName = "list_accounts",
            Content = "[]"
        };
        await subscriber.DeliverAsync($"tool.result.{_identity.Name}",
            response.ToEnvelope("bridge", correlationId: publisher.Published[0].Envelope.CorrelationId));

        var result = await executeTask;
        Assert.IsFalse(result.IsError);
    }

    [TestMethod]
    public async Task InvokeTool_EmptyArgs_SchemaCacheUnavailable_DispatchesNormally()
    {
        // When the schema cache can't supply a schema (cold cache miss against an
        // unreachable bridge, unknown tool, etc.) the executor must fall through to
        // the existing dispatch path — the MCP server's own error is still useful.
        var (executor, publisher, subscriber) = CreateExecutor();   // no schema cache wired

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-noschema",
            ToolName = "mcp_invoke_tool",
            Arguments = """{"server_name":"filesystem","tool_name":"send_email"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count,
            "Without a schema cache the executor must still dispatch.");

        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-noschema",
            ToolName = "send_email",
            Content = "ok"
        };
        await subscriber.DeliverAsync($"tool.result.{_identity.Name}",
            response.ToEnvelope("bridge", correlationId: publisher.Published[0].Envelope.CorrelationId));

        await executeTask;
    }

    // ── mcp_register_server ──────────────────────────────────────────────────

    [TestMethod]
    public async Task RegisterServer_PublishesToManageTopicAndReturnsSuccess()
    {
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_register_server",
            Arguments = """{"name":"new-server","type":"sse","url":"http://localhost:3000/sse"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count);
        Assert.AreEqual(McpManagementExecutor.ManageTopic, publisher.Published[0].Topic);

        var published = publisher.Published[0].Envelope;
        Assert.AreEqual(typeof(McpRegisterServerRequest).FullName, published.MessageType);

        // Simulate bridge success response
        var response = new McpRegisterServerResponse
        {
            ServerName = "new-server",
            Success = true,
            Summary = "Provides 3 tools."
        };
        var responseEnvelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync(executor.ResponseTopic, responseEnvelope);

        var result = await executeTask;
        Assert.IsFalse(result.IsError);
        Assert.IsTrue(result.Content!.Contains("new-server"));
        Assert.IsTrue(result.Content.Contains("registered successfully"));
    }

    [TestMethod]
    public async Task RegisterServer_MissingUrl_ReturnsError()
    {
        var (executor, _, _) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_register_server",
            Arguments = """{"name":"x","type":"sse"}"""
        };

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.Content!.Contains("url"));
    }

    // ── mcp_unregister_server ────────────────────────────────────────────────

    [TestMethod]
    public async Task UnregisterServer_PublishesToManageTopicAndReturnsSuccess()
    {
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_unregister_server",
            Arguments = """{"server_name":"filesystem"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count);
        var published = publisher.Published[0].Envelope;
        Assert.AreEqual(typeof(McpUnregisterServerRequest).FullName, published.MessageType);

        // Simulate bridge response
        var response = new McpUnregisterServerResponse
        {
            ServerName = "filesystem",
            Success = true
        };
        var responseEnvelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync(executor.ResponseTopic, responseEnvelope);

        var result = await executeTask;
        Assert.IsFalse(result.IsError);
        Assert.IsTrue(result.Content!.Contains("filesystem"));
        Assert.IsTrue(result.Content.Contains("removed successfully"));
    }

    [TestMethod]
    public async Task UnregisterServer_Timeout_ReturnsError()
    {
        var (executor, _, _) = CreateExecutor(timeout: TimeSpan.FromMilliseconds(100));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_unregister_server",
            Arguments = """{"server_name":"filesystem"}"""
        };

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.Content!.Contains("Timed out"));
    }

    // ── ResponseTopic ────────────────────────────────────────────────────────

    [TestMethod]
    public void ResponseTopic_IncludesAgentName()
    {
        var (executor, _, _) = CreateExecutor();
        Assert.AreEqual($"mcp.manage.response.{_identity.Name}", executor.ResponseTopic);
    }

    // ── case-insensitive server_name ─────────────────────────────────────────

    [TestMethod]
    public async Task GetServiceDetails_UppercaseServerName_NormalizesToLowercase()
    {
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_get_service_details",
            Arguments = """{"server_name":"FileSystem"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count);
        var published = publisher.Published[0].Envelope;
        var req = published.GetPayload<McpGetServiceDetailsRequest>();
        Assert.IsNotNull(req);
        Assert.AreEqual("filesystem", req.ServerName);

        // Simulate bridge response
        var response = new McpGetServiceDetailsResponse
        {
            ServerName = "filesystem",
            Tools = [new McpToolDefinition { Name = "read_file", Description = "Reads a file" }]
        };
        var responseEnvelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync(executor.ResponseTopic, responseEnvelope);

        var result = await executeTask;
        Assert.IsFalse(result.IsError);
    }

    [TestMethod]
    public async Task InvokeTool_UppercaseServerName_NormalizesToLowercaseInHeader()
    {
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_invoke_tool",
            Arguments = """{"server_name":"FileSystem","tool_name":"read_file"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count);
        var published = publisher.Published[0].Envelope;
        Assert.AreEqual("filesystem", published.Headers[McpHeaders.ServerName]);

        // Simulate response
        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-1",
            ToolName = "read_file",
            Content = "file contents"
        };
        var responseEnvelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync($"tool.result.{_identity.Name}", responseEnvelope);

        var result = await executeTask;
        Assert.IsFalse(result.IsError);
    }

    [TestMethod]
    public async Task UnregisterServer_UppercaseServerName_NormalizesToLowercase()
    {
        var (executor, publisher, subscriber) = CreateExecutor();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "mcp_unregister_server",
            Arguments = """{"server_name":"FileSystem"}"""
        };

        var executeTask = executor.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        Assert.AreEqual(1, publisher.Published.Count);
        var published = publisher.Published[0].Envelope;
        var req = published.GetPayload<McpUnregisterServerRequest>();
        Assert.IsNotNull(req);
        Assert.AreEqual("filesystem", req.ServerName);

        // Simulate bridge response
        var response = new McpUnregisterServerResponse { ServerName = "filesystem", Success = true };
        var responseEnvelope = response.ToEnvelope("bridge", correlationId: published.CorrelationId);
        await subscriber.DeliverAsync(executor.ResponseTopic, responseEnvelope);

        var result = await executeTask;
        Assert.IsFalse(result.IsError);
    }
}
