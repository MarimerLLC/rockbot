using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

/// <summary>
/// Tests for <see cref="A2ATaskResultHandler"/>'s working-memory side effects.
///
/// These exercise the non-user-session short-circuit (PrimarySessionId =
/// "session/subagent-..."), where the handler writes to working memory and
/// returns before touching the LLM, conversation memory, or publisher — so we
/// can leave all the heavyweight dependencies as <c>null!</c> and still cover
/// the data-part preservation path end-to-end.
/// </summary>
[TestClass]
public class A2ATaskResultHandlerTests
{
    private const string TargetAgent = "AdvisorCouncil";
    private const string TaskId = "task-42";
    private const string SubagentSession = "session/subagent-test";

    private readonly InMemoryWorkingMemory _memory = new();
    private readonly A2ATaskTracker _tracker = new();
    private readonly TrackingPublisher _publisher = new();
    private readonly A2AOptions _options = new();
    private readonly AgentIdentity _agent = new("primary-agent");
    private readonly AgentNameHolder _nameHolder = new();

    private A2ATaskResultHandler CreateHandler() =>
        new(
            agentLoopRunner: null!,
            agentContextBuilder: null!,
            llmClient: null!,
            publisher: _publisher,
            agent: _agent,
            workingMemory: _memory,
            memoryTools: null!,
            skillStore: null!,
            toolRegistry: null!,
            rulesTools: null!,
            toolGuideTools: null!,
            conversationMemory: null!,
            tracker: _tracker,
            modelBehavior: null!,
            agentNameHolder: _nameHolder,
            inputRequiredHandler: null!,
            a2aOptions: _options,
            clientCapabilityStore: new SessionClientCapabilityStore(),
            logger: NullLogger<A2ATaskResultHandler>.Instance);

    private void TrackPending() =>
        _tracker.Track(new PendingA2ATask
        {
            TaskId = TaskId,
            TargetAgent = TargetAgent,
            Skill = "deliberate",
            PrimarySessionId = SubagentSession,
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource()
        });

    private static MessageHandlerContext CreateContext(MessageEnvelope envelope) =>
        new()
        {
            Envelope = envelope,
            Agent = new AgentIdentity("primary-agent"),
            Services = null!,
            CancellationToken = default
        };

    private static AgentTaskResult CreateResult(params AgentMessagePart[] parts) =>
        new()
        {
            TaskId = TaskId,
            State = AgentTaskState.Completed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = parts
            }
        };

    [TestMethod]
    public async Task MultiPartResult_StoresDataPart_UnderSiblingKey()
    {
        const string dataJson = "{\"personas\":[\"a\",\"b\"],\"confidence\":\"high\"}";

        TrackPending();
        var result = CreateResult(
            new AgentMessagePart { Kind = "text", Text = "Synthesis prose." },
            new AgentMessagePart { Kind = "data", Data = dataJson, MimeType = "application/json" });
        var envelope = TestEnvelopeHelper.CreateEnvelope(result, correlationId: TaskId);

        await CreateHandler().HandleAsync(result, CreateContext(envelope));

        Assert.AreEqual(2, _memory.Writes.Count, "expected text + data writes");

        var textWrite = _memory.Writes[0];
        Assert.AreEqual($"{SubagentSession}/a2a/{TargetAgent}/{TaskId}/result", textWrite.Key);
        Assert.AreEqual("Synthesis prose.", textWrite.Value);
        Assert.AreEqual("a2a-result", textWrite.Category);

        var dataWrite = _memory.Writes[1];
        Assert.AreEqual($"{SubagentSession}/a2a/{TargetAgent}/{TaskId}/result.data", dataWrite.Key);
        Assert.AreEqual(dataJson, dataWrite.Value);
        Assert.AreEqual("a2a-result-data", dataWrite.Category);
        Assert.IsNotNull(dataWrite.Tags);
        CollectionAssert.Contains((System.Collections.ICollection)dataWrite.Tags!, "application/json");
        CollectionAssert.Contains((System.Collections.ICollection)dataWrite.Tags!, TargetAgent);
        CollectionAssert.Contains((System.Collections.ICollection)dataWrite.Tags!, TaskId);
    }

    [TestMethod]
    public async Task SingleTextResult_StoresOnlyResultKey()
    {
        TrackPending();
        var result = CreateResult(
            new AgentMessagePart { Kind = "text", Text = "Just prose." });
        var envelope = TestEnvelopeHelper.CreateEnvelope(result, correlationId: TaskId);

        await CreateHandler().HandleAsync(result, CreateContext(envelope));

        Assert.AreEqual(1, _memory.Writes.Count, "single-text-part path must remain unchanged");
        Assert.AreEqual($"{SubagentSession}/a2a/{TargetAgent}/{TaskId}/result", _memory.Writes[0].Key);
        Assert.AreEqual("a2a-result", _memory.Writes[0].Category);
    }

    [TestMethod]
    public async Task DataPartWithEmptyData_IsIgnored()
    {
        TrackPending();
        var result = CreateResult(
            new AgentMessagePart { Kind = "text", Text = "Prose." },
            new AgentMessagePart { Kind = "data", Data = null, MimeType = "application/json" });
        var envelope = TestEnvelopeHelper.CreateEnvelope(result, correlationId: TaskId);

        await CreateHandler().HandleAsync(result, CreateContext(envelope));

        Assert.AreEqual(1, _memory.Writes.Count, "data part with null Data should not create a .data entry");
        Assert.IsFalse(_memory.Writes.Any(w => w.Key.EndsWith("/result.data")));
    }
}

/// <summary>
/// In-memory <see cref="IWorkingMemory"/> that records every <c>SetAsync</c>
/// call for assertions. Read APIs return empty results — sufficient for the
/// non-user-session path under test.
/// </summary>
internal sealed class InMemoryWorkingMemory : IWorkingMemory
{
    public List<(string Key, string Value, string? Category, IReadOnlyList<string>? Tags)> Writes { get; } = [];

    public Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        Writes.Add((key, value, category, tags));
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

    public Task DeleteAsync(string key) => Task.CompletedTask;

    public Task ClearAsync(string? prefix = null) => Task.CompletedTask;

    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(
        MemorySearchCriteria criteria, string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
}
