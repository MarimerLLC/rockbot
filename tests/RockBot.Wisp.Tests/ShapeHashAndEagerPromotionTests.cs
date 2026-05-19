using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Wisp.Tests;

[TestClass]
public class ShapeHashTests
{
    [TestMethod]
    public void ShapeHash_IgnoresDescriptionDifferences()
    {
        var a = MakeDef(description: "Calendar events for marimer-work today and tomorrow",
            paramsJson: """{"accountId":"marimer-work","startDate":"2026-05-19"}""");
        var b = MakeDef(description: "Calendar events for lhotka.net tomorrow and day-after",
            paramsJson: """{"accountId":"lhotka.net","startDate":"2026-05-20"}""");

        Assert.AreEqual(
            SpawnWispsExecutor.ComputeShapeHash(a),
            SpawnWispsExecutor.ComputeShapeHash(b),
            "Same step shape with different descriptions/values should share a shape hash");
    }

    [TestMethod]
    public void ShapeHash_DiffersWhenParamKeysDiffer()
    {
        var a = MakeDef("x", """{"accountId":"a","startDate":"d"}""");
        var b = MakeDef("x", """{"accountId":"a","endDate":"d"}""");

        Assert.AreNotEqual(
            SpawnWispsExecutor.ComputeShapeHash(a),
            SpawnWispsExecutor.ComputeShapeHash(b),
            "Different param key sets should produce different shape hashes");
    }

    [TestMethod]
    public void ShapeHash_DiffersWhenToolDiffers()
    {
        var a = MakeDef("x", """{"q":"v"}""", tool: "get_calendar_events");
        var b = MakeDef("x", """{"q":"v"}""", tool: "search_emails");

        Assert.AreNotEqual(
            SpawnWispsExecutor.ComputeShapeHash(a),
            SpawnWispsExecutor.ComputeShapeHash(b));
    }

    [TestMethod]
    public void ShapeHash_StableAcrossParamKeyOrder()
    {
        var a = MakeDef("x", """{"accountId":"a","startDate":"d","timeZone":"z"}""");
        var b = MakeDef("x", """{"timeZone":"z","accountId":"a","startDate":"d"}""");

        Assert.AreEqual(
            SpawnWispsExecutor.ComputeShapeHash(a),
            SpawnWispsExecutor.ComputeShapeHash(b),
            "Param key order must not affect the shape hash");
    }

    [TestMethod]
    public void ShapeHash_DiffersFromDefinitionHashSemantics()
    {
        var a = MakeDef(description: "alpha", paramsJson: """{"k":"v1"}""");
        var b = MakeDef(description: "beta",  paramsJson: """{"k":"v2"}""");

        var defJsonA = JsonSerializer.Serialize(a, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var defJsonB = JsonSerializer.Serialize(b, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.AreNotEqual(
            SpawnWispsExecutor.ComputeDefinitionHash(defJsonA),
            SpawnWispsExecutor.ComputeDefinitionHash(defJsonB),
            "Definition hashes should differ across cosmetic/value drift");
        Assert.AreEqual(
            SpawnWispsExecutor.ComputeShapeHash(a),
            SpawnWispsExecutor.ComputeShapeHash(b),
            "Shape hash must collapse cosmetic/value drift");
    }

    private static WispDefinition MakeDef(string description, string paramsJson, string tool = "get_calendar_events")
    {
        var paramsEl = JsonDocument.Parse(paramsJson).RootElement;
        return new WispDefinition
        {
            Description = description,
            Steps =
            [
                new WispStep
                {
                    Id = "s1",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Mcp,
                    Server = "calendar-mcp",
                    Tool = tool,
                    Params = paramsEl.Clone()
                }
            ]
        };
    }
}

[TestClass]
public class EagerPromotionTests
{
    [TestMethod]
    public async Task PatrolSession_SecondSuccess_AttachesProvisionalResource()
    {
        var (executor, registry, log, skills, usage) = Build();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "x", Source = "web" },
            new FakeToolExecutor("ok"));

        // Skill exists and was the most recently invoked skill in this session.
        await skills.SaveAsync(new Skill("calendar/scan", "scan", "# scan", DateTimeOffset.UtcNow));
        usage.Append("patrol/heartbeat-patrol", "calendar/scan", DateTimeOffset.UtcNow.AddMinutes(-1));

        // First run records the shape; second run should trip eager promotion.
        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await WaitForBackground();

        var saved = await skills.GetAsync("calendar/scan");
        var entry = saved!.Manifest!.SingleOrDefault();
        Assert.IsNotNull(entry, "Eager promotion should have attached a single resource on the second success");
        Assert.IsTrue(entry.Provisional, "Eager-attached resources land as provisional");
        Assert.AreEqual(SkillResourceType.Wisp, entry.Type);
        StringAssert.StartsWith(entry.Filename, "eager-");
        Assert.IsNotNull(entry.DefinitionHash);
    }

    [TestMethod]
    public async Task PatrolSession_FirstSuccess_DoesNotAttach()
    {
        var (executor, registry, _, skills, usage) = Build();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "x", Source = "web" },
            new FakeToolExecutor("ok"));

        await skills.SaveAsync(new Skill("calendar/scan", "scan", "# scan", DateTimeOffset.UtcNow));
        usage.Append("patrol/heartbeat-patrol", "calendar/scan", DateTimeOffset.UtcNow.AddMinutes(-1));

        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await WaitForBackground();

        var saved = await skills.GetAsync("calendar/scan");
        Assert.IsTrue(saved!.Manifest is null or { Count: 0 },
            "Single success is below the eager threshold");
    }

    [TestMethod]
    public async Task NonPatrolSession_TwoSuccesses_DoesNotAttach()
    {
        var (executor, registry, _, skills, usage) = Build();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "x", Source = "web" },
            new FakeToolExecutor("ok"));

        await skills.SaveAsync(new Skill("calendar/scan", "scan", "# scan", DateTimeOffset.UtcNow));
        usage.Append("session/blazor-session", "calendar/scan", DateTimeOffset.UtcNow.AddMinutes(-1));

        await Spawn(executor, "session/blazor-session", paramsJson: """{"q":"v"}""");
        await Spawn(executor, "session/blazor-session", paramsJson: """{"q":"v"}""");
        await WaitForBackground();

        var saved = await skills.GetAsync("calendar/scan");
        Assert.IsTrue(saved!.Manifest is null or { Count: 0 },
            "Non-patrol sessions should NOT trigger eager promotion");
    }

    [TestMethod]
    public async Task PatrolSession_NoInvokingSkill_DoesNotAttach()
    {
        var (executor, registry, _, skills, _) = Build();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "x", Source = "web" },
            new FakeToolExecutor("ok"));

        // No usage events recorded — no invoking skill to attach to.
        await skills.SaveAsync(new Skill("calendar/scan", "scan", "# scan", DateTimeOffset.UtcNow));

        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await WaitForBackground();

        var saved = await skills.GetAsync("calendar/scan");
        Assert.IsTrue(saved!.Manifest is null or { Count: 0 });
    }

    [TestMethod]
    public async Task PatrolSession_AlreadyAttached_DoesNotReAttach()
    {
        var (executor, registry, _, skills, usage) = Build();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "x", Source = "web" },
            new FakeToolExecutor("ok"));

        await skills.SaveAsync(new Skill("calendar/scan", "scan", "# scan", DateTimeOffset.UtcNow));
        usage.Append("patrol/heartbeat-patrol", "calendar/scan", DateTimeOffset.UtcNow.AddMinutes(-1));

        // Two runs → first eager attach. Then two more runs of the same shape → no extra entries.
        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await WaitForBackground();
        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await Spawn(executor, "patrol/heartbeat-patrol", paramsJson: """{"q":"v"}""");
        await WaitForBackground();

        var saved = await skills.GetAsync("calendar/scan");
        Assert.AreEqual(1, saved!.Manifest!.Count,
            "A second pair of runs with the same shape should not produce another attachment");
    }

    [TestMethod]
    public async Task PatrolSession_DifferentDescriptions_StillAttachOnceBySharedShape()
    {
        var (executor, registry, _, skills, usage) = Build();

        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "x", Source = "web" },
            new FakeToolExecutor("ok"));

        await skills.SaveAsync(new Skill("calendar/scan", "scan", "# scan", DateTimeOffset.UtcNow));
        usage.Append("patrol/heartbeat-patrol", "calendar/scan", DateTimeOffset.UtcNow.AddMinutes(-1));

        // Two runs whose descriptions and param values differ but whose shape is identical.
        await Spawn(executor, "patrol/heartbeat-patrol",
            description: "Today's events for marimer-work", paramsJson: """{"q":"a"}""");
        await Spawn(executor, "patrol/heartbeat-patrol",
            description: "Tomorrow's events for lhotka.net", paramsJson: """{"q":"b"}""");
        await WaitForBackground();

        var saved = await skills.GetAsync("calendar/scan");
        Assert.AreEqual(1, saved!.Manifest!.Count,
            "Shape-hash grouping should collapse cosmetically distinct runs into one promotion");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (SpawnWispsExecutor executor, FakeToolRegistry registry,
                    TrackingWispExecutionLog log, InMemorySkillStore skills, InMemorySkillUsageStore usage)
        Build()
    {
        var registry = new FakeToolRegistry();
        var memory = new FakeWorkingMemory();
        var options = new WispOptions(); // eager promotion default-on, threshold 2
        var wispExecutor = new WispExecutor(registry, memory, agentLoopRunner: null!, options,
            NullLogger<WispExecutor>.Instance);
        var log = new TrackingWispExecutionLog();
        var skills = new InMemorySkillStore();
        var usage = new InMemorySkillUsageStore();
        var executor = new SpawnWispsExecutor(
            wispExecutor, log, feedbackStore: null, memory, options,
            NullLogger<SpawnWispsExecutor>.Instance,
            skillStore: skills, skillUsageStore: usage);
        return (executor, registry, log, skills, usage);
    }

    private static async Task Spawn(
        SpawnWispsExecutor executor, string sessionId,
        string paramsJson = """{"q":"v"}""",
        string description = "Shape test")
    {
        var request = new ToolInvokeRequest
        {
            ToolCallId = Guid.NewGuid().ToString("N")[..8],
            ToolName = "spawn_wisps",
            SessionId = sessionId,
            Arguments = $$"""
            {
              "definitions": [
                {
                  "description": "{{description}}",
                  "steps": [
                    { "id": "s1", "mode": "Direct", "gateway": "Web", "tool": "web_search", "params": {{paramsJson}} }
                  ]
                }
              ]
            }
            """
        };
        var response = await executor.ExecuteAsync(request, CancellationToken.None);
        Assert.IsFalse(response.IsError, response.Content);
    }

    private static Task WaitForBackground() => Task.Delay(150);
}

/// <summary>
/// Wisp execution log fake that actually returns appended records from
/// <see cref="QueryRecentAsync"/>, so eager-promotion code can count prior
/// successes. The <see cref="FakeWispExecutionLog"/> in the sibling test file
/// returns empty unconditionally — fine for batch-id tests, useless for these.
/// </summary>
internal sealed class TrackingWispExecutionLog : IWispExecutionLog
{
    public List<WispExecutionRecord> Records { get; } = [];

    public Task AppendAsync(WispExecutionRecord record, CancellationToken ct)
    {
        lock (Records) { Records.Add(record); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
        DateTimeOffset since, int maxResults, CancellationToken ct)
    {
        lock (Records)
        {
            return Task.FromResult<IReadOnlyList<WispExecutionRecord>>(
                Records.Where(r => r.Timestamp >= since)
                       .OrderBy(r => r.Timestamp)
                       .Take(maxResults)
                       .ToList());
        }
    }

    public Task<WispExecutionRecord?> FindRecentFailureAsync(
        string definitionHash, string? sessionId, CancellationToken ct)
    {
        lock (Records)
        {
            return Task.FromResult(Records
                .Where(r => !r.Succeeded && r.DefinitionHash == definitionHash)
                .OrderByDescending(r => r.Timestamp)
                .FirstOrDefault());
        }
    }
}

internal sealed class InMemorySkillStore : ISkillStore
{
    private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _resources = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(Skill skill)
    {
        lock (_skills) { _skills[skill.Name] = skill; }
        return Task.CompletedTask;
    }

    public Task SaveAsync(Skill skill, IReadOnlyList<SkillResourceInput>? resources) => SaveAsync(skill);

    public Task<Skill?> GetAsync(string name)
    {
        lock (_skills) { return Task.FromResult(_skills.GetValueOrDefault(name)); }
    }

    public Task<IReadOnlyList<Skill>> ListAsync()
    {
        lock (_skills)
        {
            return Task.FromResult<IReadOnlyList<Skill>>(_skills.Values.OrderBy(s => s.Name).ToList());
        }
    }

    public Task DeleteAsync(string name)
    {
        lock (_skills) { _skills.Remove(name); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Skill>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
        Task.FromResult<IReadOnlyList<Skill>>([]);

    public Task<string?> GetResourceAsync(string skillName, string filename)
    {
        lock (_skills)
        {
            if (_resources.TryGetValue(skillName, out var files) && files.TryGetValue(filename, out var content))
                return Task.FromResult<string?>(content);
            return Task.FromResult<string?>(null);
        }
    }

    public Task<bool> AttachResourceAsync(
        string skillName, SkillResourceInput resource, SkillResource? manifestEntry = null)
    {
        lock (_skills)
        {
            if (!_skills.TryGetValue(skillName, out var existing))
                return Task.FromResult(false);

            if (!_resources.TryGetValue(skillName, out var files))
                _resources[skillName] = files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            files[resource.Filename] = resource.Content;

            var entry = manifestEntry ?? new SkillResource(
                resource.Filename, resource.Type, resource.Description,
                Provisional: resource.Provisional,
                CreatedAt: DateTimeOffset.UtcNow,
                VerifyHint: resource.VerifyHint);

            var oldManifest = existing.Manifest ?? [];
            var newManifest = new List<SkillResource>(oldManifest.Count + 1);
            var replaced = false;
            foreach (var old in oldManifest)
            {
                if (string.Equals(old.Filename, resource.Filename, StringComparison.OrdinalIgnoreCase))
                {
                    newManifest.Add(entry);
                    replaced = true;
                }
                else
                {
                    newManifest.Add(old);
                }
            }
            if (!replaced)
                newManifest.Add(entry);

            _skills[skillName] = existing with { Manifest = newManifest };
            return Task.FromResult(true);
        }
    }
}

internal sealed class InMemorySkillUsageStore : ISkillUsageStore
{
    private readonly List<SkillInvocationEvent> _events = [];

    public void Append(string sessionId, string skillName, DateTimeOffset ts)
    {
        lock (_events)
        {
            _events.Add(new SkillInvocationEvent(
                Guid.NewGuid().ToString("N")[..8], skillName, sessionId, ts));
        }
    }

    public Task AppendAsync(SkillInvocationEvent evt, CancellationToken ct = default)
    {
        lock (_events) { _events.Add(evt); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SkillInvocationEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_events)
        {
            return Task.FromResult<IReadOnlyList<SkillInvocationEvent>>(
                _events.Where(e => e.SessionId == sessionId).ToList());
        }
    }

    public Task<IReadOnlyList<SkillInvocationEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default)
    {
        lock (_events)
        {
            return Task.FromResult<IReadOnlyList<SkillInvocationEvent>>(
                _events.Where(e => e.Timestamp >= since).OrderBy(e => e.Timestamp).Take(maxResults).ToList());
        }
    }
}
