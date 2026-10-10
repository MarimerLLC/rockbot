using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Subagent.Tests;

/// <summary>
/// A new subagent starts with what earlier subagents in the conversation produced (#665): the
/// prior-work block, its relevance ordering and budget, spawn inputs, and the iteration floor.
/// </summary>
[TestClass]
public class SubagentLineageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static SubagentWorkResult Result(string taskId, string description, string output,
        int minutesAgo, bool success = true, params string[] keys) =>
        SubagentWorkResult.Create(taskId, description, output, keys, success, Now.AddMinutes(-minutesAgo));

    private static SessionWorkSnapshot Snapshot(params SubagentWorkResult[] results) =>
        new("s1", results.OrderByDescending(r => r.CompletedAt).ToList(), []);

    private static Func<string, Task<string?>> Loader(Dictionary<string, string> values) =>
        key => Task.FromResult(values.TryGetValue(key, out var v) ? v : null);

    private static Task<string?> Build(SessionWorkSnapshot snapshot, string description,
        Dictionary<string, string>? values = null, IReadOnlyList<SubagentInput>? inputs = null,
        int budget = 24_000, string? request = null) =>
        SubagentLineage.BuildAsync(snapshot, description, null, request, inputs ?? [], budget,
            Loader(values ?? []), Now);

    // ── The block ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SecondSubagent_GetsTheResearchSummaryInlinedAndItsKeyListed()
    {
        const string key = "subagent/r1/mcp-v2-authoritative-research";
        var snapshot = Snapshot(Result("r1", "Research the MCP v2 spec from primary sources",
            "Research complete; findings saved.", 10, keys: key));

        var text = (await Build(snapshot, "Outline a talk on the MCP v2 spec",
            new() { [key] = "The 2026-07-28 spec adds server/discover and a stateless core." }))!;

        StringAssert.StartsWith(text, SubagentLineage.Heading);
        StringAssert.Contains(text, SubagentLineage.GroundingInstruction);
        StringAssert.Contains(text, "task r1");
        StringAssert.Contains(text, $"`{key}` (inlined below)");
        StringAssert.Contains(text, "server/discover");
        StringAssert.Contains(text, "Summary: Research complete; findings saved.");
    }

    [TestMethod]
    public async Task Relevance_PutsTheOverlappingResultFirst_AndLeavesUnrelatedOnesListedOnly()
    {
        var snapshot = Snapshot(
            Result("mail", "Triage unread email across all accounts", "EMAIL-OUTPUT", 1),
            Result("mcp", "Research the MCP v2 spec changes", "MCP-OUTPUT", 30),
            Result("mcp2", "Verify MCP v2 server discovery details", "MCP2-OUTPUT", 20));

        var text = (await Build(snapshot, "Create a Slidev deck on the MCP v2 spec and server discovery"))!;

        var full = text[text.IndexOf("### Full text", StringComparison.Ordinal)..];
        Assert.IsTrue(full.IndexOf("MCP2-OUTPUT", StringComparison.Ordinal) < full.IndexOf("MCP-OUTPUT", StringComparison.Ordinal),
            "more overlap ranks first");
        Assert.IsFalse(full.Contains("EMAIL-OUTPUT"), "an unrelated result is not inlined");
        StringAssert.Contains(text, "task mail", "but it is still listed");
    }

    [TestMethod]
    public void Relevance_TieGoesToTheMostRecent()
    {
        var older = Result("old", "Research MCP", "x", 30);
        var newer = Result("new", "Research MCP", "y", 5);

        var ranked = SubagentLineage.RankForInlining([older, newer], SubagentLineage.Tokenize("mcp deck"));

        CollectionAssert.AreEqual(new[] { "new", "old" }, ranked.Select(r => r.TaskId).ToArray());
    }

    [TestMethod]
    public void Relevance_NoOverlap_KeepsOnlyTheMostRecentSuccessfulResult()
    {
        var ranked = SubagentLineage.RankForInlining(
        [
            Result("a", "Find the venue address", "x", 30),
            Result("b", "Look up flight times", "y", 5),
            Result("c", "Check hotel", "z", 1, success: false),
        ], SubagentLineage.Tokenize("Create a Slidev deck"));

        CollectionAssert.AreEqual(new[] { "b" }, ranked.Select(r => r.TaskId).ToArray());
    }

    [TestMethod]
    public async Task Budget_TruncatesTheInlinedTextAndSaysWhereTheRestIs()
    {
        const string key = "subagent/r1/research";
        var snapshot = Snapshot(Result("r1", "Research the MCP v2 spec", "short", 10, keys: key));

        var text = (await Build(snapshot, "Outline the MCP v2 spec talk",
            new() { [key] = new string('R', 10_000) }, budget: 2_000))!;

        var inlined = text.Count(c => c == 'R');
        Assert.IsTrue(inlined is > 0 and < 2_000, $"inlined {inlined} chars");
        StringAssert.Contains(text, $"truncated to fit — get_from_working_memory('{key}')");
        StringAssert.Contains(text, "(partly inlined below)");
    }

    [TestMethod]
    public async Task ZeroBudget_ListsPointersButInlinesNothing()
    {
        const string key = "subagent/r1/research";
        var snapshot = Snapshot(Result("r1", "Research the MCP v2 spec", "OUTPUT", 10, keys: key));

        var text = (await Build(snapshot, "Outline the MCP v2 talk", new() { [key] = "CONTENT" }, budget: 0))!;

        StringAssert.Contains(text, key);
        Assert.IsFalse(text.Contains("CONTENT"));
        Assert.IsFalse(text.Contains("### Full text"));
    }

    [TestMethod]
    public async Task FailedResults_AreListedButNotInlined()
    {
        var snapshot = Snapshot(Result("f1", "Research the MCP v2 spec", "FAILED-OUTPUT", 3, success: false));

        var text = (await Build(snapshot, "Research the MCP v2 spec again"))!;

        StringAssert.Contains(text, "task f1 (FAILED");
        Assert.IsFalse(text.Contains("### Full text"));
    }

    [TestMethod]
    public async Task Inputs_AreInlinedFirst_WithinTheSameBudget()
    {
        const string key = "subagent/r1/research";
        var snapshot = Snapshot(Result("r1", "Research the MCP v2 spec", new string('O', 3_000), 10, keys: key));
        var inputs = new[] { new SubagentInput("drafts/outline.md", SubagentInputKind.File, "OUTLINE " + new string('I', 1_500)) };

        var text = (await Build(snapshot, "Build the MCP v2 deck", new() { [key] = new string('K', 3_000) },
            inputs, budget: 2_500))!;

        StringAssert.Contains(text, "### Inputs you must use");
        StringAssert.Contains(text, "file `drafts/outline.md` (inlined in full below)");
        var full = text[text.IndexOf("### Full text", StringComparison.Ordinal)..];
        Assert.IsTrue(full.IndexOf("#### Input: drafts/outline.md", StringComparison.Ordinal) == full.IndexOf("####", StringComparison.Ordinal),
            "the input comes first");
        Assert.IsFalse(text.Contains(new string('K', 100)), "the budget ran out before the result's key");
    }

    [TestMethod]
    public async Task NothingButTheRequest_GivesAShortRequestBlock()
    {
        var text = await Build(SessionWorkSnapshot.Empty("s1"), "Research X", request: "what changed in MCP v2?");

        StringAssert.StartsWith(text!, "## The user's request");
        StringAssert.Contains(text, "what changed in MCP v2?");
    }

    [TestMethod]
    public async Task NothingAtAll_IsNull()
    {
        Assert.IsNull(await Build(SessionWorkSnapshot.Empty("s1"), "Research X"));
    }

    [TestMethod]
    public async Task Artifacts_AreListedWithWriterAndRemoteTarget()
    {
        var snapshot = new SessionWorkSnapshot("s1", [],
        [
            new SessionArtifact("drafts/deck.md", "subagent-abc", "file_write", "onedrive:/Talks/deck.md", false, Now),
        ]);

        var text = (await Build(snapshot, "Polish the deck"))!;

        StringAssert.Contains(text, "drafts/deck.md — last written by subagent-abc (file_write), uploaded to onedrive:/Talks/deck.md");
    }

    // ── Inputs ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Inputs_MissingKey_FailsWithSiblingKeysAsSuggestions()
    {
        var memory = new FakeWorkingMemory();
        memory.Values["subagent/e951ad95c4c1/talk-outline"] = "outline";
        memory.Values["subagent/16493b466542/mcp-v2-authoritative-research"] = "research";
        var resolver = new SubagentInputResolver(memory, new FakeToolRegistry());

        var resolution = await resolver.ResolveAsync(
            ["subagent/e951ad95c4c1/mcp-v2-authoritative-research"], "session/s1", CancellationToken.None);

        Assert.IsNotNull(resolution.Error);
        StringAssert.Contains(resolution.Error, "no subagent was spawned");
        StringAssert.Contains(resolution.Error, "'subagent/e951ad95c4c1/talk-outline'", "same namespace");
        StringAssert.Contains(resolution.Error, "'subagent/16493b466542/mcp-v2-authoritative-research'", "same name, other task");
    }

    [TestMethod]
    public async Task Inputs_MissingFile_SuggestsTheSameFileNameElsewhere()
    {
        var registry = new FakeToolRegistry();
        registry.Files["Talks/techorama/deck.md"] = "# Deck";
        var resolver = new SubagentInputResolver(new FakeWorkingMemory(), registry);

        var resolution = await resolver.ResolveAsync(["drafts/deck.md"], "session/s1", CancellationToken.None);

        StringAssert.Contains(resolution.Error!, "'Talks/techorama/deck.md'");
    }

    [TestMethod]
    public async Task Inputs_FilePathAndPlainKey_Resolve()
    {
        var memory = new FakeWorkingMemory();
        memory.Values["session/s1/notes"] = "NOTES";
        var registry = new FakeToolRegistry();
        registry.Files["drafts/outline.md"] = "# Outline";
        var resolver = new SubagentInputResolver(memory, registry);

        var resolution = await resolver.ResolveAsync(["notes", "drafts/outline.md"], "session/s1", CancellationToken.None);

        Assert.IsNull(resolution.Error);
        Assert.AreEqual(2, resolution.Inputs.Count);
        Assert.AreEqual("session/s1/notes", resolution.Inputs[0].Reference);
        Assert.AreEqual(SubagentInputKind.WorkingMemory, resolution.Inputs[0].Kind);
        Assert.AreEqual(SubagentInputKind.File, resolution.Inputs[1].Kind);
        Assert.AreEqual("# Outline", resolution.Inputs[1].Content);
    }

    [TestMethod]
    public async Task SpawnExecutor_MissingInput_FailsFastWithoutSpawning()
    {
        var manager = new RecordingSubagentManager();
        var executor = new SpawnSubagentExecutor(manager,
            new SubagentInputResolver(new FakeWorkingMemory(), new FakeToolRegistry()));

        var response = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "c1",
            ToolName = "spawn_subagent",
            SessionId = "session/s1",
            Arguments = JsonSerializer.Serialize(new { description = "Build the deck", inputs = new[] { "subagent/nope/key" } }),
        }, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "subagent/nope/key");
        Assert.AreEqual(0, manager.SpawnCount);
    }

    [TestMethod]
    public async Task SpawnExecutor_ValidInputs_ReachTheManager()
    {
        var registry = new FakeToolRegistry();
        registry.Files["drafts/outline.md"] = "# Outline";
        var manager = new RecordingSubagentManager();
        var executor = new SpawnSubagentExecutor(manager, new SubagentInputResolver(new FakeWorkingMemory(), registry));

        var response = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "c1",
            ToolName = "spawn_subagent",
            SessionId = "session/s1",
            // Some models stringify arrays.
            Arguments = JsonSerializer.Serialize(new { description = "Build the deck", inputs = "[\"drafts/outline.md\"]" }),
        }, CancellationToken.None);

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("drafts/outline.md", manager.LastInputs!.Single().Reference);
    }

    // ── Iteration floor ──────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("Research the MCP v2 spec", 8, 20)]
    [DataRow("Investigate why the build fails", 5, 20)]
    [DataRow("Verify the dates against the spec", 10, 20)]
    [DataRow("Synthesize the findings into an outline", 12, 20)]
    [DataRow("Outline a 60-minute talk", 8, 20)]
    [DataRow("Draft the speaker notes", 8, 20)]
    [DataRow("Write a Slidev deck from the outline", 8, 20)]
    [DataRow("Create a document summarising the call", 8, 20)]
    [DataRow("Research the MCP v2 spec", 30, 30)]
    [DataRow("Check my calendar for tomorrow", 8, 8)]
    [DataRow("Send the email to Bob", 3, 3)]
    public void IterationFloor_RaisesResearchAndSynthesisTasksOnly(string description, int requested, int expected)
    {
        Assert.AreEqual(expected, SubagentManager.ApplyResearchIterationFloor(description, requested, floor: 20));
    }

    [TestMethod]
    public void IterationFloor_LeavesAnUnsetCapAlone_AndCanBeDisabled()
    {
        Assert.IsNull(SubagentManager.ApplyResearchIterationFloor("Research X", null, 20));
        Assert.AreEqual(8, SubagentManager.ApplyResearchIterationFloor("Research X", 8, 0));
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeWorkingMemory : IWorkingMemory
    {
        public Dictionary<string, string> Values { get; } = new();

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null,
            IReadOnlyList<string>? tags = null)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>(Values
                .Where(kv => prefix is null || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => new WorkingMemoryEntry(kv.Key, kv.Value, Now, Now.AddHours(4)))
                .ToList());

        public Task DeleteAsync(string key) { Values.Remove(key); return Task.CompletedTask; }
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            ListAsync(prefix);
    }

    /// <summary>A registry exposing file_read / file_list over an in-memory file set.</summary>
    private sealed class FakeToolRegistry : IToolRegistry
    {
        public Dictionary<string, string> Files { get; } = new();

        public IReadOnlyList<ToolRegistration> GetTools() => [];
        public void Register(ToolRegistration registration, IToolExecutor executor) { }
        public bool Unregister(string toolName) => false;

        public IToolExecutor? GetExecutor(string toolName) => toolName switch
        {
            "file_read" => new FileExecutor(this, read: true),
            "file_list" => new FileExecutor(this, read: false),
            _ => null,
        };

        private sealed class FileExecutor(FakeToolRegistry owner, bool read) : IToolExecutor
        {
            public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
            {
                if (!read)
                    return Task.FromResult(Ok(request, JsonSerializer.Serialize(owner.Files.Keys.OrderBy(k => k).ToList())));

                var path = JsonDocument.Parse(request.Arguments!).RootElement.GetProperty("path").GetString()!;
                return Task.FromResult(owner.Files.TryGetValue(path, out var content)
                    ? Ok(request, content)
                    : new ToolInvokeResponse
                    {
                        ToolCallId = request.ToolCallId, ToolName = request.ToolName,
                        Content = $"File not found: {path}", IsError = true,
                    });
            }

            private static ToolInvokeResponse Ok(ToolInvokeRequest r, string content) =>
                new() { ToolCallId = r.ToolCallId, ToolName = r.ToolName, Content = content, IsError = false };
        }
    }

    private sealed class RecordingSubagentManager : ISubagentManager
    {
        public int SpawnCount { get; private set; }
        public IReadOnlyList<SubagentInput>? LastInputs { get; private set; }

        public Task<string> SpawnAsync(string description, string? context, int? timeoutMinutes,
            string primarySessionId, CancellationToken ct, string? batchId = null, bool consolidate = true,
            int? maxIterations = null, string? originatingUserRequest = null,
            IReadOnlyList<SubagentInput>? inputs = null)
        {
            SpawnCount++;
            LastInputs = inputs;
            return Task.FromResult("task123");
        }

        public Task<bool> CancelAsync(string taskId) => Task.FromResult(false);
        public IReadOnlyList<SubagentEntry> ListActive() => [];
    }
}
