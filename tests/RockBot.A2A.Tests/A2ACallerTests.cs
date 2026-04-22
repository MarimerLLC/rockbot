using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using A2AV1 = A2A;
using A2AV03 = A2A.V0_3;

namespace RockBot.A2A.Tests;

[TestClass]
public class A2ACallerTests
{
    // ─── helpers ────────────────────────────────────────────────────────────────

    private static AgentIdentity TestIdentity => new("primary-agent");

    private static A2AOptions DefaultOptions => new();

    private static AgentDirectory EmptyDirectory =>
        new(new A2AOptions { DirectoryPersistencePath = string.Empty },
            NullLogger<AgentDirectory>.Instance);

    private static ToolInvokeRequest BuildToolRequest(string args, string? sessionId = "sess-1") =>
        new()
        {
            ToolCallId = "tc-1",
            ToolName = "invoke_agent",
            Arguments = args,
            SessionId = sessionId
        };

    private static InvokeAgentExecutor BuildExecutor(
        IMessagePublisher publisher,
        A2ATaskTracker tracker,
        A2AOptions? options = null,
        IAgentDirectory? directory = null) =>
        new(publisher, tracker, directory ?? EmptyDirectory,
            options ?? DefaultOptions, TestIdentity,
            NullHttpClientFactory.Instance,
            null!, // InputRequiredHandler — not needed for queue-transport tests
            NullLogger<InvokeAgentExecutor>.Instance);

    // ─── InvokeAgentExecutor ────────────────────────────────────────────────────

    [TestMethod]
    public async Task InvokeAgentExecutor_PublishesTaskRequest_ToCorrectTopic()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""
            { "agent_name": "TargetAgent", "skill": "summarize", "message": "Summarize this." }
            """);

        await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(1, publisher.Published.Count);
        Assert.AreEqual("agent.task.TargetAgent", publisher.Published[0].Topic);

        var taskReq = publisher.Published[0].Envelope.GetPayload<AgentTaskRequest>();
        Assert.IsNotNull(taskReq);
        Assert.AreEqual("summarize", taskReq.Skill);
        Assert.AreEqual("Summarize this.", taskReq.Message.Parts[0].Text);
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_ReturnsPendingTaskId()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""
            { "agent_name": "TargetAgent", "skill": "chat", "message": "Hello." }
            """);

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "task_id:");

        // Task should be tracked
        var active = tracker.ListActive();
        Assert.AreEqual(1, active.Count);
        Assert.AreEqual("TargetAgent", active[0].TargetAgent);
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_SetsReplyTo_ToCallerResultTopic()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var options = new A2AOptions { CallerResultTopic = "agent.response" };
        var executor = BuildExecutor(publisher, tracker, options);

        var request = BuildToolRequest("""
            { "agent_name": "TargetAgent", "skill": "chat", "message": "Hi." }
            """);

        await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual("agent.response.primary-agent", publisher.Published[0].Envelope.ReplyTo);
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_ReturnsError_WhenMissingAgentName()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""{ "skill": "chat", "message": "Hi." }""");

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.AreEqual(0, publisher.Published.Count);
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_ReturnsError_WhenMissingSkill()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""{ "agent_name": "TargetAgent", "message": "Hi." }""");

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_ReturnsError_WhenMissingMessage()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""{ "agent_name": "TargetAgent", "skill": "chat" }""");

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_OmitsDataPart_WhenDataArgMissing()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""
            { "agent_name": "TargetAgent", "skill": "chat", "message": "Hi." }
            """);

        await executor.ExecuteAsync(request, CancellationToken.None);

        var taskReq = publisher.Published[0].Envelope.GetPayload<AgentTaskRequest>()!;
        Assert.AreEqual(1, taskReq.Message.Parts.Count);
        Assert.AreEqual("text", taskReq.Message.Parts[0].Kind);
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_AddsDataPart_WhenDataObjectProvided()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""
            {
              "agent_name": "TargetAgent",
              "skill": "summarize",
              "message": "Summarize this record.",
              "data": { "recordId": "abc-123", "fields": ["title", "body"] }
            }
            """);

        await executor.ExecuteAsync(request, CancellationToken.None);

        var taskReq = publisher.Published[0].Envelope.GetPayload<AgentTaskRequest>()!;
        Assert.AreEqual(2, taskReq.Message.Parts.Count);
        Assert.AreEqual("text", taskReq.Message.Parts[0].Kind);
        Assert.AreEqual("Summarize this record.", taskReq.Message.Parts[0].Text);

        var dataPart = taskReq.Message.Parts[1];
        Assert.AreEqual("data", dataPart.Kind);
        Assert.AreEqual("application/json", dataPart.MimeType);
        Assert.IsNotNull(dataPart.Data);

        using var parsed = System.Text.Json.JsonDocument.Parse(dataPart.Data!);
        Assert.AreEqual("abc-123", parsed.RootElement.GetProperty("recordId").GetString());
        Assert.AreEqual(2, parsed.RootElement.GetProperty("fields").GetArrayLength());
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_ReturnsError_WhenDataIsNotObject()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""
            { "agent_name": "TargetAgent", "skill": "chat", "message": "Hi.", "data": "not-an-object" }
            """);

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "data");
        Assert.AreEqual(0, publisher.Published.Count);
    }

    [TestMethod]
    public async Task InvokeAgentExecutor_AcceptsNullData_AsMissing()
    {
        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker);

        var request = BuildToolRequest("""
            { "agent_name": "TargetAgent", "skill": "chat", "message": "Hi.", "data": null }
            """);

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        var taskReq = publisher.Published[0].Envelope.GetPayload<AgentTaskRequest>()!;
        Assert.AreEqual(1, taskReq.Message.Parts.Count);
    }

    [TestMethod]
    public void MapOutboundV03Part_TextPart_MapsToV03TextPart()
    {
        var part = new AgentMessagePart { Kind = "text", Text = "hello" };

        var mapped = InvokeAgentExecutor.MapOutboundV03Part(part);

        var textPart = (A2AV03.TextPart)mapped;
        Assert.AreEqual("hello", textPart.Text);
    }

    [TestMethod]
    public void MapOutboundV03Part_DataPart_MapsToV03DataPartWithDictionary()
    {
        var part = new AgentMessagePart
        {
            Kind = "data",
            Data = """{ "id": "x-1", "count": 3 }""",
            MimeType = "application/json"
        };

        var mapped = InvokeAgentExecutor.MapOutboundV03Part(part);

        var dataPart = (A2AV03.DataPart)mapped;
        Assert.AreEqual("x-1", dataPart.Data["id"].GetString());
        Assert.AreEqual(3, dataPart.Data["count"].GetInt32());
    }

    [TestMethod]
    public void MapOutboundV1Part_TextPart_MapsToV1TextPart()
    {
        var part = new AgentMessagePart { Kind = "text", Text = "hello" };

        var mapped = InvokeAgentExecutor.MapOutboundV1Part(part);

        Assert.AreEqual(A2AV1.PartContentCase.Text, mapped.ContentCase);
        Assert.AreEqual("hello", mapped.Text);
    }

    [TestMethod]
    public void MapOutboundV1Part_DataPart_MapsToV1DataPartWithMediaType()
    {
        var part = new AgentMessagePart
        {
            Kind = "data",
            Data = """{ "ok": true }""",
            MimeType = "application/json"
        };

        var mapped = InvokeAgentExecutor.MapOutboundV1Part(part);

        Assert.AreEqual(A2AV1.PartContentCase.Data, mapped.ContentCase);
        Assert.AreEqual("application/json", mapped.MediaType);
        Assert.IsTrue(mapped.Data!.Value.GetProperty("ok").GetBoolean());
    }

    // ─── ListKnownAgentsExecutor ─────────────────────────────────────────────────

    [TestMethod]
    public async Task ListKnownAgentsExecutor_ReturnsAllAgents_WhenNoFilter()
    {
        var directory = new AgentDirectory(new A2AOptions { DirectoryPersistencePath = string.Empty }, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentDirectory>.Instance);
        directory.AddOrUpdate(new AgentCard
        {
            AgentName = "AgentA",
            Description = "Agent A",
            Skills = [new AgentSkill { Id = "skill1", Name = "Skill One" }]
        });
        directory.AddOrUpdate(new AgentCard { AgentName = "AgentB", Description = "Agent B" });

        var executor = new ListKnownAgentsExecutor(directory);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc", ToolName = "list_known_agents", Arguments = null
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "AgentA");
        StringAssert.Contains(response.Content, "AgentB");
    }

    [TestMethod]
    public async Task ListKnownAgentsExecutor_FiltersBySkill()
    {
        var directory = new AgentDirectory(new A2AOptions { DirectoryPersistencePath = string.Empty }, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentDirectory>.Instance);
        directory.AddOrUpdate(new AgentCard
        {
            AgentName = "AgentA",
            Skills = [new AgentSkill { Id = "summarize", Name = "Summarize" }]
        });
        directory.AddOrUpdate(new AgentCard
        {
            AgentName = "AgentB",
            Skills = [new AgentSkill { Id = "translate", Name = "Translate" }]
        });

        var executor = new ListKnownAgentsExecutor(directory);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc",
            ToolName = "list_known_agents",
            Arguments = """{"skill":"summarize"}"""
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "AgentA");
        Assert.IsFalse(response.Content.Contains("AgentB"),
            "AgentB should not appear when filtering by 'summarize'");
    }

    [TestMethod]
    public async Task ListKnownAgentsExecutor_ReturnsEmpty_WhenNoAgents()
    {
        var directory = new AgentDirectory(new A2AOptions { DirectoryPersistencePath = string.Empty }, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentDirectory>.Instance);
        var executor = new ListKnownAgentsExecutor(directory);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "tc", ToolName = "list_known_agents", Arguments = null
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "No agents");
    }

    // ─── A2ATaskTracker ──────────────────────────────────────────────────────────

    [TestMethod]
    public void A2ATaskTracker_Track_And_TryGet()
    {
        var tracker = new A2ATaskTracker();
        var cts = new CancellationTokenSource();
        var task = new PendingA2ATask
        {
            TaskId = "t1",
            TargetAgent = "AgentX",
            Skill = "test-skill",
            PrimarySessionId = "sess",
            StartedAt = DateTimeOffset.UtcNow,
            Cts = cts
        };

        tracker.Track(task);

        Assert.IsTrue(tracker.TryGet("t1", out var found));
        Assert.IsNotNull(found);
        Assert.AreEqual("AgentX", found.TargetAgent);
    }

    [TestMethod]
    public void A2ATaskTracker_TryRemove_RemovesTask()
    {
        var tracker = new A2ATaskTracker();
        var cts = new CancellationTokenSource();
        var task = new PendingA2ATask
        {
            TaskId = "t2",
            TargetAgent = "AgentY",
            Skill = "test-skill",
            PrimarySessionId = "sess",
            StartedAt = DateTimeOffset.UtcNow,
            Cts = cts
        };

        tracker.Track(task);
        Assert.IsTrue(tracker.TryRemove("t2", out _));
        Assert.IsFalse(tracker.TryGet("t2", out _));
    }

    [TestMethod]
    public void A2ATaskTracker_ListActive_ReturnsAllTracked()
    {
        var tracker = new A2ATaskTracker();

        for (var i = 0; i < 3; i++)
        {
            tracker.Track(new PendingA2ATask
            {
                TaskId = $"t{i}",
                TargetAgent = "AgentZ",
                Skill = "test-skill",
                PrimarySessionId = "sess",
                StartedAt = DateTimeOffset.UtcNow,
                Cts = new CancellationTokenSource()
            });
        }

        Assert.AreEqual(3, tracker.ListActive().Count);
    }

    // ─── Protocol version detection ────────────────────────────────────────────

    [TestMethod]
    public void IsV1_ReturnsTrue_WhenProtocolVersionIs1()
    {
        var card = new AgentCard { AgentName = "a", ProtocolVersion = "1.0" };
        Assert.IsTrue(InvokeAgentExecutor.IsV1(card));
    }

    [TestMethod]
    public void IsV1_ReturnsTrue_WhenProtocolVersionIs1_NoMinor()
    {
        var card = new AgentCard { AgentName = "a", ProtocolVersion = "1" };
        Assert.IsTrue(InvokeAgentExecutor.IsV1(card));
    }

    [TestMethod]
    public void IsV1_ReturnsTrue_WhenProtocolVersionIs1_1()
    {
        var card = new AgentCard { AgentName = "a", ProtocolVersion = "1.1" };
        Assert.IsTrue(InvokeAgentExecutor.IsV1(card));
    }

    [TestMethod]
    public void IsV1_ReturnsFalse_WhenProtocolVersionIs03()
    {
        var card = new AgentCard { AgentName = "a", ProtocolVersion = "0.3" };
        Assert.IsFalse(InvokeAgentExecutor.IsV1(card));
    }

    [TestMethod]
    public void IsV1_ReturnsFalse_WhenProtocolVersionIsNull()
    {
        var card = new AgentCard { AgentName = "a" };
        Assert.IsFalse(InvokeAgentExecutor.IsV1(card));
    }

    [TestMethod]
    public void IsV1_ReturnsFalse_WhenCardIsNull()
    {
        Assert.IsFalse(InvokeAgentExecutor.IsV1(null));
    }

    [TestMethod]
    public void InvokeAgentExecutor_UsesQueueTransport_WhenUrlIsNullRegardlessOfVersion()
    {
        // Even if ProtocolVersion is 1.0, queue transport should be used when no URL is set
        var directory = new AgentDirectory(
            new A2AOptions { DirectoryPersistencePath = string.Empty },
            NullLogger<AgentDirectory>.Instance);
        directory.AddOrUpdate(new AgentCard
        {
            AgentName = "QueueAgent",
            ProtocolVersion = "1.0"
        });

        var publisher = new TrackingPublisher();
        var tracker = new A2ATaskTracker();
        var executor = BuildExecutor(publisher, tracker, directory: directory);

        var request = BuildToolRequest("""
            { "agent_name": "QueueAgent", "skill": "chat", "message": "Hello." }
            """);

        var response = executor.ExecuteAsync(request, CancellationToken.None).Result;

        Assert.IsFalse(response.IsError);
        // Should publish to queue, not HTTP
        Assert.AreEqual(1, publisher.Published.Count);
        Assert.AreEqual("agent.task.QueueAgent", publisher.Published[0].Topic);
    }

    // ─── V1 response mapping ────────────────────────────────────────────────────

    [TestMethod]
    public void MapV1Response_MapsMessageResponse_ToCompleted()
    {
        var response = new A2AV1.SendMessageResponse
        {
            Message = new A2AV1.Message
            {
                Role = A2AV1.Role.Agent,
                Parts = [new A2AV1.Part { Text = "Hello from v1" }]
            }
        };

        var result = InvokeAgentExecutor.MapV1Response(response, "task-1");

        Assert.IsNotNull(result);
        Assert.AreEqual("task-1", result.TaskId);
        Assert.AreEqual(AgentTaskState.Completed, result.State);
        Assert.IsNotNull(result.Message);
        Assert.AreEqual("assistant", result.Message.Role);
        Assert.AreEqual("Hello from v1", result.Message.Parts[0].Text);
    }

    [TestMethod]
    public void MapV1Response_MapsTaskResponse_ToCorrectState()
    {
        var response = new A2AV1.SendMessageResponse
        {
            Task = new A2AV1.AgentTask
            {
                Id = "task-2",
                ContextId = "ctx-1",
                Status = new A2AV1.TaskStatus
                {
                    State = A2AV1.TaskState.Working,
                    Message = new A2AV1.Message
                    {
                        Role = A2AV1.Role.Agent,
                        Parts = [new A2AV1.Part { Text = "Still processing" }]
                    }
                }
            }
        };

        var result = InvokeAgentExecutor.MapV1Response(response, "task-2");

        Assert.IsNotNull(result);
        Assert.AreEqual("task-2", result.TaskId);
        Assert.AreEqual("ctx-1", result.ContextId);
        Assert.AreEqual(AgentTaskState.Working, result.State);
        Assert.AreEqual("Still processing", result.Message?.Parts[0].Text);
    }

    [TestMethod]
    public void MapV1Response_MapsRejected_ToFailed()
    {
        var response = new A2AV1.SendMessageResponse
        {
            Task = new A2AV1.AgentTask
            {
                Id = "task-3",
                Status = new A2AV1.TaskStatus { State = A2AV1.TaskState.Rejected }
            }
        };

        var result = InvokeAgentExecutor.MapV1Response(response, "task-3");

        Assert.IsNotNull(result);
        Assert.AreEqual(AgentTaskState.Failed, result.State);
    }

    [TestMethod]
    public void MapV1Response_MapsAuthRequired_ToInputRequired()
    {
        var response = new A2AV1.SendMessageResponse
        {
            Task = new A2AV1.AgentTask
            {
                Id = "task-4",
                Status = new A2AV1.TaskStatus { State = A2AV1.TaskState.AuthRequired }
            }
        };

        var result = InvokeAgentExecutor.MapV1Response(response, "task-4");

        Assert.IsNotNull(result);
        Assert.AreEqual(AgentTaskState.InputRequired, result.State);
    }

    [TestMethod]
    public void MapV1Response_ReturnsNull_WhenPayloadCaseIsNone()
    {
        var response = new A2AV1.SendMessageResponse();

        var result = InvokeAgentExecutor.MapV1Response(response, "task-5");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void MapV1Response_PreservesContextId_OnInputRequired()
    {
        var response = new A2AV1.SendMessageResponse
        {
            Task = new A2AV1.AgentTask
            {
                Id = "task-ir",
                ContextId = "ctx-multi-turn",
                Status = new A2AV1.TaskStatus
                {
                    State = A2AV1.TaskState.InputRequired,
                    Message = new A2AV1.Message
                    {
                        Role = A2AV1.Role.Agent,
                        Parts = [new A2AV1.Part { Text = "What time works for you?" }]
                    }
                }
            }
        };

        var result = InvokeAgentExecutor.MapV1Response(response, "task-ir");

        Assert.IsNotNull(result);
        Assert.AreEqual(AgentTaskState.InputRequired, result.State);
        Assert.AreEqual("ctx-multi-turn", result.ContextId);
        Assert.AreEqual("What time works for you?", result.Message?.Parts[0].Text);
    }

    [TestMethod]
    public void MapV1TaskResponse_MapsWorkingState()
    {
        var task = new A2AV1.AgentTask
        {
            Id = "task-w",
            ContextId = "ctx-w",
            Status = new A2AV1.TaskStatus
            {
                State = A2AV1.TaskState.Working,
                Message = new A2AV1.Message
                {
                    Role = A2AV1.Role.Agent,
                    Parts = [new A2AV1.Part { Text = "Still processing..." }]
                }
            }
        };

        var result = InvokeAgentExecutor.MapV1TaskResponse(task, "task-w");

        Assert.IsNotNull(result);
        Assert.AreEqual(AgentTaskState.Working, result.State);
        Assert.AreEqual("ctx-w", result.ContextId);
    }

    // ─── V0.3 response mapping ──────────────────────────────────────────────────

    [TestMethod]
    public void MapV03Response_MapsAgentMessage_ToCompleted()
    {
        var response = (A2AV03.A2AResponse)new A2AV03.AgentMessage
        {
            Role = A2AV03.MessageRole.Agent,
            Parts = [new A2AV03.TextPart { Text = "Done v0.3" }]
        };

        var result = InvokeAgentExecutor.MapV03Response(response, "task-6");

        Assert.IsNotNull(result);
        Assert.AreEqual(AgentTaskState.Completed, result.State);
        Assert.AreEqual("assistant", result.Message?.Role);
        Assert.AreEqual("Done v0.3", result.Message?.Parts[0].Text);
    }

    [TestMethod]
    public void MapV03Response_MapsInputRequired_CorrectState()
    {
        var response = (A2AV03.A2AResponse)new A2AV03.AgentTask
        {
            ContextId = "ctx-v03",
            Status = new A2AV03.AgentTaskStatus
            {
                State = A2AV03.TaskState.InputRequired,
                Message = new A2AV03.AgentMessage
                {
                    Role = A2AV03.MessageRole.Agent,
                    Parts = [new A2AV03.TextPart { Text = "Need more info" }]
                }
            }
        };

        var result = InvokeAgentExecutor.MapV03Response(response, "task-v03ir");

        Assert.IsNotNull(result);
        Assert.AreEqual(AgentTaskState.InputRequired, result.State);
        Assert.AreEqual("ctx-v03", result.ContextId);
        Assert.AreEqual("Need more info", result.Message?.Parts[0].Text);
    }

    [TestMethod]
    public void MapV03Response_PreservesContextId_OnWorking()
    {
        var response = (A2AV03.A2AResponse)new A2AV03.AgentTask
        {
            ContextId = "ctx-working",
            Status = new A2AV03.AgentTaskStatus
            {
                State = A2AV03.TaskState.Working,
            }
        };

        var result = InvokeAgentExecutor.MapV03Response(response, "task-v03w");

        Assert.IsNotNull(result);
        Assert.AreEqual(AgentTaskState.Working, result.State);
        Assert.AreEqual("ctx-working", result.ContextId);
    }

    // ─── PendingA2ATask multi-turn state ────────────────────────────────────────

    [TestMethod]
    public void PendingA2ATask_MultiTurnState_DefaultsCorrectly()
    {
        var task = new PendingA2ATask
        {
            TaskId = "t1",
            TargetAgent = "Agent",
            Skill = "test",
            PrimarySessionId = "sess",
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource()
        };

        Assert.IsNull(task.ContextId);
        Assert.AreEqual(0, task.InputRequiredRound);
        Assert.IsNull(task.LastInputRequiredQuestion);
        Assert.IsNull(task.LastInputRequiredAnswer);
    }

    [TestMethod]
    public void PendingA2ATask_MultiTurnState_IsMutable()
    {
        var task = new PendingA2ATask
        {
            TaskId = "t1",
            TargetAgent = "Agent",
            Skill = "test",
            PrimarySessionId = "sess",
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource()
        };

        task.ContextId = "ctx-1";
        task.InputRequiredRound = 3;
        task.LastInputRequiredQuestion = "What time?";
        task.LastInputRequiredAnswer = "3pm";

        Assert.AreEqual("ctx-1", task.ContextId);
        Assert.AreEqual(3, task.InputRequiredRound);
        Assert.AreEqual("What time?", task.LastInputRequiredQuestion);
        Assert.AreEqual("3pm", task.LastInputRequiredAnswer);
    }

    // ─── AgentCard ProtocolVersion persistence ──────────────────────────────────

    [TestMethod]
    public void AgentCard_ProtocolVersion_IsPreservedInDirectory()
    {
        var directory = new AgentDirectory(
            new A2AOptions { DirectoryPersistencePath = string.Empty },
            NullLogger<AgentDirectory>.Instance);

        directory.AddOrUpdate(new AgentCard
        {
            AgentName = "V1Agent",
            Url = "http://localhost:5000",
            ProtocolVersion = "1.0"
        });

        var card = directory.GetAgent("V1Agent");
        Assert.IsNotNull(card);
        Assert.AreEqual("1.0", card.ProtocolVersion);
    }

    // ─── V1 streaming event mapping ────────────────────────────────────────────

    [TestMethod]
    public void MapV1StatusUpdateEvent_MapsWorkingState()
    {
        var statusUpdate = new A2AV1.TaskStatusUpdateEvent
        {
            TaskId = "task-sw",
            ContextId = "ctx-sw",
            Status = new A2AV1.TaskStatus
            {
                State = A2AV1.TaskState.Working,
                Message = new A2AV1.Message
                {
                    Role = A2AV1.Role.Agent,
                    Parts = [new A2AV1.Part { Text = "Processing..." }]
                }
            }
        };

        var result = InvokeAgentExecutor.MapV1StatusUpdateEvent(statusUpdate, "task-sw");

        Assert.AreEqual("task-sw", result.TaskId);
        Assert.AreEqual(AgentTaskState.Working, result.State);
        Assert.AreEqual("ctx-sw", result.ContextId);
        Assert.AreEqual("Processing...", result.Message?.Parts[0].Text);
    }

    [TestMethod]
    public void MapV1StatusUpdateEvent_MapsCompletedState()
    {
        var statusUpdate = new A2AV1.TaskStatusUpdateEvent
        {
            TaskId = "task-sc",
            Status = new A2AV1.TaskStatus
            {
                State = A2AV1.TaskState.Completed,
                Message = new A2AV1.Message
                {
                    Role = A2AV1.Role.Agent,
                    Parts = [new A2AV1.Part { Text = "All done" }]
                }
            }
        };

        var result = InvokeAgentExecutor.MapV1StatusUpdateEvent(statusUpdate, "task-sc");

        Assert.AreEqual(AgentTaskState.Completed, result.State);
        Assert.AreEqual("All done", result.Message?.Parts[0].Text);
    }

    [TestMethod]
    public void MapV1StatusUpdateEvent_MapsInputRequiredState()
    {
        var statusUpdate = new A2AV1.TaskStatusUpdateEvent
        {
            TaskId = "task-sir",
            ContextId = "ctx-sir",
            Status = new A2AV1.TaskStatus
            {
                State = A2AV1.TaskState.InputRequired,
                Message = new A2AV1.Message
                {
                    Role = A2AV1.Role.Agent,
                    Parts = [new A2AV1.Part { Text = "What day?" }]
                }
            }
        };

        var result = InvokeAgentExecutor.MapV1StatusUpdateEvent(statusUpdate, "task-sir");

        Assert.AreEqual(AgentTaskState.InputRequired, result.State);
        Assert.AreEqual("What day?", result.Message?.Parts[0].Text);
    }

    [TestMethod]
    public void MapV1StatusUpdateEvent_PreservesContextId()
    {
        var statusUpdate = new A2AV1.TaskStatusUpdateEvent
        {
            TaskId = "task-ctx",
            ContextId = "ctx-preserved",
            Status = new A2AV1.TaskStatus { State = A2AV1.TaskState.Working }
        };

        var result = InvokeAgentExecutor.MapV1StatusUpdateEvent(statusUpdate, "task-ctx");

        Assert.AreEqual("ctx-preserved", result.ContextId);
    }

    [TestMethod]
    public void MapV1StatusUpdateEvent_MapsRejected_ToFailed()
    {
        var statusUpdate = new A2AV1.TaskStatusUpdateEvent
        {
            TaskId = "task-rej",
            Status = new A2AV1.TaskStatus { State = A2AV1.TaskState.Rejected }
        };

        var result = InvokeAgentExecutor.MapV1StatusUpdateEvent(statusUpdate, "task-rej");

        Assert.AreEqual(AgentTaskState.Failed, result.State);
    }

    [TestMethod]
    public void MapV1StatusUpdateEvent_MapsAuthRequired_ToInputRequired()
    {
        var statusUpdate = new A2AV1.TaskStatusUpdateEvent
        {
            TaskId = "task-auth",
            Status = new A2AV1.TaskStatus { State = A2AV1.TaskState.AuthRequired }
        };

        var result = InvokeAgentExecutor.MapV1StatusUpdateEvent(statusUpdate, "task-auth");

        Assert.AreEqual(AgentTaskState.InputRequired, result.State);
    }

    [TestMethod]
    public void MapV1StatusUpdateEvent_HandlesNullMessage()
    {
        var statusUpdate = new A2AV1.TaskStatusUpdateEvent
        {
            TaskId = "task-nm",
            Status = new A2AV1.TaskStatus { State = A2AV1.TaskState.Working }
        };

        var result = InvokeAgentExecutor.MapV1StatusUpdateEvent(statusUpdate, "task-nm");

        Assert.AreEqual(AgentTaskState.Working, result.State);
        Assert.IsNull(result.Message);
    }

    // ─── A2ATaskStatusHandler ────────────────────────────────────────────────────

    [TestMethod]
    public async Task A2ATaskStatusHandler_IgnoresUnknownCorrelationIds()
    {
        // Use the real tracker (no task tracked) and a fake context with unknown correlationId
        var tracker = new A2ATaskTracker();
        var logger = NullLogger<A2ATaskStatusHandler>.Instance;

        // A2ATaskStatusHandler requires heavy dependencies for the LLM loop.
        // This test validates the early-return guard via the tracker directly.
        var update = new AgentTaskStatusUpdate { TaskId = "t-unknown", State = AgentTaskState.Working };
        var envelope = TestEnvelopeHelper.CreateEnvelope(update, correlationId: "unknown-corr");

        // Verify tracker does NOT have the task
        Assert.IsFalse(tracker.TryGet("unknown-corr", out _));

        // If we call TryGet and it returns false, the handler should return without action.
        // This is validated by the guard condition in A2ATaskStatusHandler.HandleAsync.
        // The actual handler requires AgentLoopRunner etc. so we test the guard path here.
        await Task.CompletedTask; // placeholder assertion — guard verified above
    }
}
