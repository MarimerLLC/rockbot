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
    public void Resolve_ReturnsNull_WhenTheNamedResponderIsNotRegistered()
        => Assert.IsNull(Resolve("reserach", Services()),
            "a typo must leave the server with no responder, not fall back to the default");

    [TestMethod]
    public void Resolve_ReturnsNull_WhenAResponderIsNamedButThereAreNoServices()
        => Assert.IsNull(Resolve("research", services: null));
}
