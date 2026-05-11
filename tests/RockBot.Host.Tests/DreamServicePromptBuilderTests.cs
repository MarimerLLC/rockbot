using RockBot.Tools;

namespace RockBot.Host.Tests;

/// <summary>
/// Verifies that <see cref="DreamService.BuildSkillConsolidationUserMessage"/> respects the
/// <see cref="ConsolidationPolicy.NamespacedSingleton"/> hint advertised by an
/// <see cref="IToolSkillProvider"/>: clusters under such prefixes must not appear in the
/// "consider an abstract parent guide" section, and a constraints paragraph must name
/// each prefix so the LLM does not propose merging across them.
/// </summary>
[TestClass]
public class DreamServicePromptBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void GetSingletonPrefixes_NullProviders_ReturnsEmpty()
    {
        var prefixes = DreamService.GetSingletonPrefixes(null);
        Assert.AreEqual(0, prefixes.Count);
    }

    [TestMethod]
    public void GetSingletonPrefixes_FiltersOutTopicalAndUnsetProviders()
    {
        var providers = new IToolSkillProvider[]
        {
            new StubProvider("mcp", ("mcp/", ConsolidationPolicy.NamespacedSingleton)),
            new StubProvider("web", null),
            new StubProvider("scripts", ("scripts/", ConsolidationPolicy.TopicalCluster))
        };

        var prefixes = DreamService.GetSingletonPrefixes(providers);

        CollectionAssert.AreEqual(new[] { "mcp/" }, prefixes.ToArray());
    }

    [TestMethod]
    public void BuildSkillConsolidationUserMessage_NoSingletonPrefix_OmitsConstraintsAndIncludesAllClusters()
    {
        var skills = new[]
        {
            MakeSkill("mcp/ms365"),
            MakeSkill("mcp/calendar-mcp"),
            MakeSkill("coding/dotnet"),
            MakeSkill("coding/python")
        };

        var msg = DreamService.BuildSkillConsolidationUserMessage(
            skills,
            usageCount: new Dictionary<string, int>(),
            coUsed: new Dictionary<string, List<string>>(),
            coOccurrences: new Dictionary<string, int>(),
            singletonPrefixes: Array.Empty<string>(),
            now: Now);

        StringAssert.Contains(msg, "'mcp/*': mcp/calendar-mcp, mcp/ms365");
        StringAssert.Contains(msg, "'coding/*': coding/dotnet, coding/python");
        Assert.IsFalse(msg.Contains("Constraints — namespaced-singleton prefixes"),
            "Without singleton prefixes the constraints block must not appear.");
    }

    [TestMethod]
    public void BuildSkillConsolidationUserMessage_WithMcpSingleton_ExcludesMcpClusterAndAppendsConstraints()
    {
        var skills = new[]
        {
            MakeSkill("mcp/ms365"),
            MakeSkill("mcp/calendar-mcp"),
            MakeSkill("coding/dotnet"),
            MakeSkill("coding/python")
        };

        var msg = DreamService.BuildSkillConsolidationUserMessage(
            skills,
            usageCount: new Dictionary<string, int>(),
            coUsed: new Dictionary<string, List<string>>(),
            coOccurrences: new Dictionary<string, int>(),
            singletonPrefixes: new[] { "mcp/" },
            now: Now);

        Assert.IsFalse(msg.Contains("'mcp/*':"),
            "mcp/* cluster must NOT appear in the abstract-parent-guide section.");
        StringAssert.Contains(msg, "'coding/*': coding/dotnet, coding/python");

        StringAssert.Contains(msg, "Constraints — namespaced-singleton prefixes");
        StringAssert.Contains(msg, "'mcp/*'");
        StringAssert.Contains(msg, "Do NOT merge skills across distinct suffixes");
    }

    [TestMethod]
    public void BuildSkillConsolidationUserMessage_WithMcpSingleton_AllowsWithinNamespaceMerging()
    {
        // The constraint message must explicitly permit merging duplicate sub-skills
        // within a single mcp/{server} namespace, while still forbidding cross-suffix merges.
        var skills = new[]
        {
            MakeSkill("mcp/calendar-mcp"),
            MakeSkill("mcp/calendar-mcp/send-email"),
            MakeSkill("mcp/calendar-mcp/email-send"),
            MakeSkill("mcp/ms365"),
            MakeSkill("mcp/ms365/calendar-tools"),
        };

        var msg = DreamService.BuildSkillConsolidationUserMessage(
            skills,
            usageCount: new Dictionary<string, int>(),
            coUsed: new Dictionary<string, List<string>>(),
            coOccurrences: new Dictionary<string, int>(),
            singletonPrefixes: new[] { "mcp/" },
            now: Now);

        // Within-namespace merging is explicitly permitted.
        StringAssert.Contains(msg, "Within a single suffix's namespace");
        StringAssert.Contains(msg, "normal semantic-overlap merging applies");

        // Cross-suffix merging is explicitly forbidden, including across sub-skills.
        StringAssert.Contains(msg, "must remain separate");
        StringAssert.Contains(msg, "must never be merged with a sub-skill or canonical entry of another");

        // Top-level mcp/* still excluded from abstract-parent-guide suggestions.
        Assert.IsFalse(msg.Contains("'mcp/*':"),
            "mcp/* cluster must NOT appear in the abstract-parent-guide section.");
    }

    private static Skill MakeSkill(string name) => new(
        Name: name,
        Summary: $"summary for {name}",
        Content: $"content for {name}",
        CreatedAt: Now.AddDays(-30));

    private sealed class StubProvider : IToolSkillProvider
    {
        private readonly (string Prefix, ConsolidationPolicy Policy)? _policy;

        public StubProvider(string name, (string Prefix, ConsolidationPolicy Policy)? policy)
        {
            Name = name;
            _policy = policy;
        }

        public string Name { get; }
        public string Summary => $"stub {Name}";
        public string GetDocument() => string.Empty;
        public (string Prefix, ConsolidationPolicy Policy)? ConsolidationPolicy => _policy;
    }
}
