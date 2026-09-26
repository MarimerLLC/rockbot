using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Tools.Tests.Elicitation;

[TestClass]
public class McpElicitationRespondersTests
{
    private sealed class NamedResponder(string name) : IMcpElicitationResponder
    {
        public string Name { get; } = name;

        public ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct)
            => ValueTask.FromResult(McpElicitationAnswer.Decline(Name));
    }

    private static readonly NamedResponder Default = new("default");
    private static readonly NamedResponder Research = new("research");

    private static IServiceProvider Services()
        => new ServiceCollection()
            .AddKeyedSingleton<IMcpElicitationResponder>("research", Research)
            .BuildServiceProvider();

    private static IMcpElicitationResponder? Resolve(string? responder, IServiceProvider? services)
        => McpElicitationResponders.Resolve(
            new McpElicitationConfig { Responder = responder }, Default, services, "research-server", NullLogger.Instance);

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Resolve_UsesTheDefault_WhenTheServerNamesNone(string? responder)
        => Assert.AreSame(Default, Resolve(responder, Services()));

    [TestMethod]
    public void Resolve_UsesTheDefault_WhenThereIsNoConfig()
        => Assert.AreSame(Default,
            McpElicitationResponders.Resolve(null, Default, Services(), "s", NullLogger.Instance));

    [TestMethod]
    public void Resolve_UsesTheNamedResponder()
        => Assert.AreSame(Research, Resolve(" research ", Services()));

    [TestMethod]
    public void Resolve_MatchesALowerCaseKeyWhateverTheConfiguredCase()
        => Assert.AreSame(Research, Resolve("Research", Services()));

    [TestMethod]
    public void Resolve_ReturnsNull_WhenTheNamedResponderIsNotRegistered()
        => Assert.IsNull(Resolve("reserach", Services()),
            "a typo must leave the server with no responder, not fall back to the default");

    [TestMethod]
    public void Resolve_ReturnsNull_WhenAResponderIsNamedButThereAreNoServices()
        => Assert.IsNull(Resolve("research", services: null));

    private sealed class OptInResponder : IMcpElicitationResponder
    {
        public bool RequiresServerOptIn => true;

        public ValueTask<McpElicitationAnswer> AnswerAsync(McpElicitationContext context, CancellationToken ct)
            => ValueTask.FromResult(McpElicitationAnswer.Decline("n/a"));
    }

    private static readonly OptInResponder Conversation = new();

    private static IServiceProvider ServicesWithOptIn()
        => new ServiceCollection()
            .AddKeyedSingleton<IMcpElicitationResponder>("conversation", Conversation)
            .BuildServiceProvider();

    [TestMethod]
    public void Resolve_AllowsAnOptInResponderInAServersOwnPolicy()
        => Assert.AreSame(Conversation, McpElicitationResponders.Resolve(
            new McpElicitationConfig { Responder = "conversation" }, Default, ServicesWithOptIn(), "s",
            NullLogger.Instance, isServerPolicy: true));

    [TestMethod]
    public void Resolve_RefusesAnOptInResponderInTheBridgeWideDefault()
    {
        // DefaultElicitation reaches every server the model registers, at URLs it chose.
        var resolved = McpElicitationResponders.Resolve(
            new McpElicitationConfig { Responder = "conversation" }, Default, ServicesWithOptIn(), "s",
            NullLogger.Instance, isServerPolicy: false);

        Assert.IsNull(resolved, "must not fall back to the default responder either — only defaults answer");
    }

    [TestMethod]
    [DataRow(true, DisplayName = "server policy with no responder named")]
    [DataRow(false, DisplayName = "bridge-wide default")]
    public void Resolve_NeverUsesAnOptInResponderAsTheImplicitDefault(bool isServerPolicy)
    {
        // A host that registers an opt-in responder as its unnamed default must not thereby
        // extend it to servers that never named it — including one left with no responder
        // by WithoutGrants().
        var resolved = McpElicitationResponders.Resolve(
            new McpElicitationConfig().WithoutGrants(), Conversation, ServicesWithOptIn(), "s",
            NullLogger.Instance, isServerPolicy);

        Assert.IsNull(resolved);
    }

    [TestMethod]
    public void Resolve_AllowsAnOrdinaryResponderInTheBridgeWideDefault()
        => Assert.AreSame(Research, McpElicitationResponders.Resolve(
            new McpElicitationConfig { Responder = "research" }, Default, Services(), "s",
            NullLogger.Instance, isServerPolicy: false));

    [TestMethod]
    public void WithoutGrants_KeepsRestrictionsAndDropsGrants()
    {
        var config = new McpElicitationConfig
        {
            Mode = "decline",
            MaxPerCall = 1,
            DeniedFields = ["accountId"],
            ResponderTimeoutMs = 5_000,
            Responder = "conversation",
            Defaults = new() { ["confirm"] = System.Text.Json.JsonDocument.Parse("true").RootElement },
        };

        var restricted = config.WithoutGrants();

        Assert.AreEqual("decline", restricted.Mode);
        Assert.AreEqual(1, restricted.MaxPerCall);
        CollectionAssert.AreEqual(new[] { "accountId" }, restricted.DeniedFields);
        Assert.AreEqual(5_000, restricted.ResponderTimeoutMs);
        Assert.IsNull(restricted.Responder);
        Assert.AreEqual(0, restricted.Defaults.Count);
        Assert.AreEqual("conversation", config.Responder, "the original is not modified");
    }
}
