using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Tools.Scheduling;
using RockBot.UserProxy;

namespace RockBot.Tools.Tests;

/// <summary>
/// Per-task zones on the scheduling tools (#740). With location off every tool must look and
/// behave exactly as it did before; with it on, <c>schedule_task</c> requires a zone.
/// </summary>
[TestClass]
public sealed class SchedulingTimeZoneTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    private static AgentClock MakeClock() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:Timezone"] = "America/Chicago" })
                .Build(),
            Options.Create(new AgentProfileOptions
            {
                BasePath = Path.Combine(Path.GetTempPath(), "rockbot-clock-" + Guid.NewGuid().ToString("N"))
            }),
            NullLogger<AgentClock>.Instance);

    private static ToolInvokeRequest Request(string json) => new()
    {
        ToolCallId = "call-1",
        ToolName = "schedule_task",
        Arguments = json
    };

    // ── schedule_task registration ────────────────────────────────────────────

    private static async Task<ToolRegistration> RegisteredScheduleTask(UserLocationState? location)
    {
        var registry = new CapturingRegistry();
        var registrar = new SchedulingToolRegistrar(
            registry, new FakeScheduler(), MakeClock(),
            NullLogger<SchedulingToolRegistrar>.Instance, location);
        await registrar.StartAsync(CancellationToken.None);
        return registry.Registrations.Single(r => r.Name == "schedule_task");
    }

    [TestMethod]
    public async Task ScheduleTask_WithoutLocation_SchemaAndDescriptionUnchanged()
    {
        var reg = await RegisteredScheduleTask(null);

        Assert.AreEqual(SchedulingToolRegistrar.BuildScheduleSchema(false), reg.ParametersSchema);
        Assert.AreEqual(SchedulingToolRegistrar.ScheduleDescription, reg.Description);

        using var schema = JsonDocument.Parse(reg.ParametersSchema!);
        Assert.IsFalse(schema.RootElement.GetProperty("properties").TryGetProperty("timeZone", out _));
        CollectionAssert.AreEqual(
            new[] { "name", "cron", "description" },
            schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.IsFalse(reg.Description.Contains("timeZone", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ScheduleTask_WithLocation_RequiresTimeZone()
    {
        var reg = await RegisteredScheduleTask(new UserLocationState(Options.Create(new UserLocationOptions())));

        using var schema = JsonDocument.Parse(reg.ParametersSchema!);
        Assert.IsTrue(schema.RootElement.GetProperty("properties").TryGetProperty("timeZone", out _));
        CollectionAssert.Contains(
            schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray(),
            "timeZone");
        StringAssert.StartsWith(reg.Description, SchedulingToolRegistrar.ScheduleDescription);
        StringAssert.Contains(reg.Description, "TIME ZONE (required)");
    }

    // ── schedule_task execution ───────────────────────────────────────────────

    [TestMethod]
    public async Task Execute_WithoutLocation_NoTimeZone_StoresNullAndKeepsOldConfirmation()
    {
        var scheduler = new FakeScheduler();
        var executor = new ScheduleTaskExecutor(scheduler, MakeClock());

        var result = await executor.ExecuteAsync(
            Request("""{"name":"t","cron":"0 8 * * *","description":"d","timeZone":"local"}"""), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.IsNull(scheduler.Saved!.TimeZone, "Without location a timeZone argument is ignored");
        StringAssert.Matches(result.Content!, new System.Text.RegularExpressions.Regex(
            @"^Scheduled task 't' with cron '0 8 \* \* \*'\. Next fire: \d{4}-\d\d-\d\d \d\d:\d\d:\d\d \(America/Chicago\)\. Current time: \d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.$"));
    }

    [TestMethod]
    public async Task Execute_WithLocation_MissingTimeZone_IsError()
    {
        var scheduler = new FakeScheduler();
        var executor = new ScheduleTaskExecutor(scheduler, MakeClock(), locationEnabled: true);

        var result = await executor.ExecuteAsync(
            Request("""{"name":"t","cron":"0 8 * * *","description":"d"}"""), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "timeZone");
        StringAssert.Contains(result.Content, "'local'");
        Assert.IsNull(scheduler.Saved);
    }

    [TestMethod]
    public async Task Execute_WithLocation_UnknownTimeZone_IsError()
    {
        var executor = new ScheduleTaskExecutor(new FakeScheduler(), MakeClock(), locationEnabled: true);

        var result = await executor.ExecuteAsync(
            Request("""{"name":"t","cron":"0 8 * * *","description":"d","timeZone":"Mars/Olympus"}"""), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "Mars/Olympus");
    }

    [TestMethod]
    public async Task Execute_RunOnceLocal_StoresConcreteCurrentZone()
    {
        var clock = MakeClock();
        clock.SetCurrentZone(Amsterdam);
        var scheduler = new FakeScheduler();
        var executor = new ScheduleTaskExecutor(scheduler, clock, locationEnabled: true);

        var result = await executor.ExecuteAsync(
            Request("""{"name":"t","cron":"30 14 30 10 *","description":"d","runOnce":true,"timeZone":"local"}"""),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("Europe/Amsterdam", scheduler.Saved!.TimeZone);
    }

    [TestMethod]
    public async Task Execute_RecurringLocal_StoresLocal_AndConfirmsZone()
    {
        var clock = MakeClock();
        clock.SetCurrentZone(Amsterdam);
        var scheduler = new FakeScheduler();
        var executor = new ScheduleTaskExecutor(scheduler, clock, locationEnabled: true);

        var result = await executor.ExecuteAsync(
            Request("""{"name":"t","cron":"0 7 * * *","description":"d","timeZone":"Local"}"""), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual("local", scheduler.Saved!.TimeZone);
        StringAssert.Contains(result.Content, "in local (Europe/Amsterdam)");
    }

    [TestMethod]
    public async Task Execute_Home_StoresHome()
    {
        var scheduler = new FakeScheduler();
        var executor = new ScheduleTaskExecutor(scheduler, MakeClock(), locationEnabled: true);

        await executor.ExecuteAsync(
            Request("""{"name":"t","cron":"0 7 * * 1","description":"d","timeZone":"home"}"""), CancellationToken.None);

        Assert.AreEqual("home", scheduler.Saved!.TimeZone);
    }

    // ── list_scheduled_tasks ──────────────────────────────────────────────────

    private static readonly ScheduledTask[] Tasks =
    [
        new("brief", "0 7 * * 1-5", "brief", DateTimeOffset.UtcNow, TimeZone: "local",
            ClientCapabilities: ClientCapabilityPresets.Blazor, Directive: "keep it short",
            LastFiredAt: DateTimeOffset.UtcNow.AddDays(-1)),
        new("guard", "0 9 * * *", "guard", DateTimeOffset.UtcNow),
        new("london", "0 9 * * *", "london", DateTimeOffset.UtcNow, TimeZone: "Europe/London"),
        new("reminder", "30 14 30 10 *", "reminder", DateTimeOffset.UtcNow, RunOnce: true, TimeZone: "Europe/Amsterdam"),
    ];

    [TestMethod]
    public async Task List_WithoutLocation_HasNoZoneLines()
    {
        var executor = new ListScheduledTasksExecutor(new FakeScheduler(Tasks), MakeClock());

        var result = await executor.ExecuteAsync(Request("{}"), CancellationToken.None);

        Assert.IsFalse(result.Content!.Contains("Time zone:"), result.Content);
        Assert.IsFalse(result.Content.Contains("Output format:"), result.Content);
        Assert.IsFalse(result.Content.Contains("Run once:"), result.Content);
        StringAssert.Contains(result.Content, "  Next fire: ");
        StringAssert.Contains(result.Content, "(America/Chicago)");
    }

    [TestMethod]
    public async Task List_WithLocation_ShowsOutputFormatAndRunOnce()
    {
        var executor = new ListScheduledTasksExecutor(new FakeScheduler(Tasks), MakeClock(), locationEnabled: true);

        var result = await executor.ExecuteAsync(Request("{}"), CancellationToken.None);

        StringAssert.Contains(result.Content, "**brief**");
        StringAssert.Contains(result.Content, "Output format: rich");
        StringAssert.Contains(result.Content, "Output format: plain");
        StringAssert.Contains(result.Content, "Run once: yes");
    }

    [TestMethod]
    public void DescribeOutputFormat_InvertsParseOutputFormat()
    {
        foreach (var name in new[] { "plain", "markdown", "rich" })
            Assert.AreEqual(name,
                ScheduleTaskExecutor.DescribeOutputFormat(ScheduleTaskExecutor.ParseOutputFormat(name)));

        // What "rich" stored before the preset gained image attachments and forms; live tasks hold it.
        Assert.AreEqual("rich", ScheduleTaskExecutor.DescribeOutputFormat((ClientCapabilities)196863));
        Assert.AreEqual("plain", ScheduleTaskExecutor.DescribeOutputFormat(ClientCapabilities.Text));
        Assert.AreEqual("markdown", ScheduleTaskExecutor.DescribeOutputFormat(
            ClientCapabilities.Text | ClientCapabilities.MarkdownTables));
    }

    // ── set_task_time_zone ────────────────────────────────────────────────────

    [TestMethod]
    public async Task SetTaskTimeZone_RegisteredOnlyWithLocation()
    {
        var without = new CapturingRegistry();
        await new SchedulingToolRegistrar(without, new FakeScheduler(), MakeClock(),
            NullLogger<SchedulingToolRegistrar>.Instance).StartAsync(CancellationToken.None);

        var with = new CapturingRegistry();
        await new SchedulingToolRegistrar(with, new FakeScheduler(), MakeClock(),
            NullLogger<SchedulingToolRegistrar>.Instance,
            new UserLocationState(Options.Create(new UserLocationOptions()))).StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "schedule_task", "cancel_scheduled_task", "list_scheduled_tasks" },
            without.Registrations.Select(r => r.Name).ToArray());
        Assert.IsTrue(with.Registrations.Any(r => r.Name == "set_task_time_zone"));
        Assert.AreEqual("scheduling", with.Registrations.Single(r => r.Name == "set_task_time_zone").Source);
        Assert.AreEqual("scheduling", RockBot.Host.DeferredToolFamilies.FamilyOf("set_task_time_zone"));
    }

    private static ToolInvokeRequest SetZoneRequest(string json) => new()
    {
        ToolCallId = "call-2",
        ToolName = "set_task_time_zone",
        Arguments = json
    };

    [TestMethod]
    public async Task SetTaskTimeZone_ChangesOnlyTheZone()
    {
        var clock = MakeClock();
        clock.SetCurrentZone(Amsterdam);
        var original = Tasks[0] with { TimeZone = null };
        var scheduler = new FakeScheduler(original);
        var executor = new SetTaskTimeZoneExecutor(scheduler, clock);

        var result = await executor.ExecuteAsync(
            SetZoneRequest("""{"name":"BRIEF","timeZone":"local"}"""), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual(original with { TimeZone = "local" }, scheduler.Saved);
        StringAssert.Contains(result.Content, "now runs in local (Europe/Amsterdam) (was home (America/Chicago))");
        StringAssert.Contains(result.Content, "cron '0 7 * * 1-5' is unchanged");
    }

    [TestMethod]
    public async Task SetTaskTimeZone_RunOnceLocal_PinsCurrentZone()
    {
        var clock = MakeClock();
        clock.SetCurrentZone(Amsterdam);
        var scheduler = new FakeScheduler(Tasks[3] with { TimeZone = "home" });
        var executor = new SetTaskTimeZoneExecutor(scheduler, clock);

        await executor.ExecuteAsync(SetZoneRequest("""{"name":"reminder","timeZone":"local"}"""), CancellationToken.None);

        Assert.AreEqual("Europe/Amsterdam", scheduler.Saved!.TimeZone);
    }

    [TestMethod]
    public async Task SetTaskTimeZone_UnknownTask_IsErrorNamingTheTasks()
    {
        var scheduler = new FakeScheduler(Tasks);
        var executor = new SetTaskTimeZoneExecutor(scheduler, MakeClock());

        var result = await executor.ExecuteAsync(
            SetZoneRequest("""{"name":"nope","timeZone":"local"}"""), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "brief, guard, london, reminder");
        Assert.IsNull(scheduler.Saved);
    }

    [TestMethod]
    public async Task SetTaskTimeZone_MissingOrInvalidZone_IsError()
    {
        var scheduler = new FakeScheduler(Tasks);
        var executor = new SetTaskTimeZoneExecutor(scheduler, MakeClock());

        var missing = await executor.ExecuteAsync(SetZoneRequest("""{"name":"brief"}"""), CancellationToken.None);
        var invalid = await executor.ExecuteAsync(
            SetZoneRequest("""{"name":"brief","timeZone":"Mars/Olympus"}"""), CancellationToken.None);

        Assert.IsTrue(missing.IsError);
        StringAssert.Contains(missing.Content, "'local'");
        Assert.IsTrue(invalid.IsError);
        StringAssert.Contains(invalid.Content, "Mars/Olympus");
        Assert.IsNull(scheduler.Saved);
    }

    // ── tool guide ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Guide_WithoutLocation_IsUnchanged()
    {
        var provider = new SchedulingToolSkillProvider();

        Assert.AreEqual(SchedulingToolSkillProvider.Document, provider.GetDocument());
        Assert.AreEqual(
            "Schedule one-time or recurring tasks with cron expressions (schedule_task, cancel_scheduled_task, list_scheduled_tasks).",
            provider.Summary);
        Assert.IsFalse(provider.GetDocument().Contains("set_task_time_zone"));
        StringAssert.Contains(provider.GetDocument(), SchedulingToolSkillProvider.ZoneAnchor);
    }

    [TestMethod]
    public void Guide_WithLocation_ExplainsZonesAndSetTaskTimeZone()
    {
        var doc = new SchedulingToolSkillProvider(
            new UserLocationState(Options.Create(new UserLocationOptions()))).GetDocument();

        StringAssert.Contains(doc, "Four tools let you");
        Assert.IsFalse(doc.Contains(SchedulingToolSkillProvider.ZoneAnchor), "the old one-zone sentence must go");
        StringAssert.Contains(doc, "### set_task_time_zone");
        StringAssert.Contains(doc, "| `local`");
        StringAssert.Contains(doc, "| `home`");
        Assert.IsTrue(doc.IndexOf("## Time Zones", StringComparison.Ordinal)
            < doc.IndexOf(SchedulingToolSkillProvider.ToolsAnchor, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task List_WithLocation_ShowsEachTasksZone()
    {
        var clock = MakeClock();
        clock.SetCurrentZone(Amsterdam);
        var executor = new ListScheduledTasksExecutor(new FakeScheduler(Tasks), clock, locationEnabled: true);

        var result = await executor.ExecuteAsync(Request("{}"), CancellationToken.None);

        StringAssert.Contains(result.Content, "Time zone: local (Europe/Amsterdam)");
        StringAssert.Contains(result.Content, "Time zone: home (America/Chicago)");
        StringAssert.Contains(result.Content, "Time zone: Europe/London");
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeScheduler(params ScheduledTask[] tasks) : ISchedulerService
    {
        public ScheduledTask? Saved { get; private set; }

        public Task ScheduleAsync(ScheduledTask task, CancellationToken ct = default)
        {
            Saved = task;
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(string name, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledTask>>(tasks);

        public DateTimeOffset? GetNextOccurrence(ScheduledTask task) => DateTimeOffset.UtcNow.AddHours(1);
    }

    private sealed class CapturingRegistry : IToolRegistry
    {
        public List<ToolRegistration> Registrations { get; } = [];

        public IReadOnlyList<ToolRegistration> GetTools() => Registrations;

        public IToolExecutor? GetExecutor(string toolName) => null;

        public void Register(ToolRegistration registration, IToolExecutor executor) =>
            Registrations.Add(registration);

        public bool Unregister(string toolName) => Registrations.RemoveAll(r => r.Name == toolName) > 0;
    }
}
