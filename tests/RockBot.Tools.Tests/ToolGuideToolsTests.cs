using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;

namespace RockBot.Tools.Tests;

/// <summary>
/// <c>get_tool_guide</c> returns the guide with the lessons its <c>{name}-learned</c> companion
/// skill has gathered (#699).
/// </summary>
[TestClass]
public class ToolGuideToolsTests
{
    private sealed class Provider : IToolSkillProvider
    {
        public string Name => "mcp";
        public string Summary => "MCP tools";
        public string GetDocument() => "# MCP guide";
    }

    [TestMethod]
    public async Task GetToolGuide_AppendsTheCompanionsLessons_AndMarksThemUsed()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("mcp-learned", "s", "Pass the server name.", DateTimeOffset.UtcNow));
        var tools = new ToolGuideTools([new Provider()], NullLogger<ToolGuideTools>.Instance, [store]);

        var guide = await tools.GetToolGuide("MCP");

        Assert.AreEqual("# MCP guide\n\n---\n## Lessons learned (skill: mcp-learned)\n\nPass the server name.", guide);
        Assert.IsNotNull((await store.GetAsync("mcp-learned"))!.LastUsedAt);
    }

    [TestMethod]
    public async Task GetToolGuide_NoCompanionOrNoStore_ReturnsTheGuideAlone()
    {
        var withStore = new ToolGuideTools([new Provider()], NullLogger<ToolGuideTools>.Instance, [new InMemorySkillStore()]);
        var withoutStore = new ToolGuideTools([new Provider()], NullLogger<ToolGuideTools>.Instance);

        Assert.AreEqual("# MCP guide", await withStore.GetToolGuide("mcp"));
        Assert.AreEqual("# MCP guide", await withoutStore.GetToolGuide("mcp"));
    }

    [TestMethod]
    public async Task GetToolGuide_StoreFailure_StillReturnsTheGuide()
    {
        var tools = new ToolGuideTools([new Provider()], NullLogger<ToolGuideTools>.Instance, [new ThrowingSkillStore()]);

        Assert.AreEqual("# MCP guide", await tools.GetToolGuide("mcp"));
    }

    private sealed class InMemorySkillStore : ISkillStore
    {
        private readonly Dictionary<string, Skill> _skills = new(StringComparer.OrdinalIgnoreCase);
        public Task SaveAsync(Skill skill) { _skills[skill.Name] = skill; return Task.CompletedTask; }
        public Task<Skill?> GetAsync(string name) => Task.FromResult(_skills.GetValueOrDefault(name));
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>(_skills.Values.ToList());
        public Task DeleteAsync(string name) { _skills.Remove(name); return Task.CompletedTask; }
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    private sealed class ThrowingSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => throw new IOException("disk gone");
        public Task<Skill?> GetAsync(string name) => throw new IOException("disk gone");
        public Task<IReadOnlyList<Skill>> ListAsync() => throw new IOException("disk gone");
        public Task DeleteAsync(string name) => throw new IOException("disk gone");
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            throw new IOException("disk gone");
    }
}
