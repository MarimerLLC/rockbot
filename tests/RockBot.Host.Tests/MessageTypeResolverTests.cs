using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class MessageTypeResolverTests
{
    [TestMethod]
    public void Register_DefaultKey_UsesFullName()
    {
        var resolver = new MessageTypeResolver();
        resolver.Register<TestMessage>();

        var result = resolver.Resolve(typeof(TestMessage).FullName!);

        Assert.AreEqual(typeof(TestMessage), result);
    }

    [TestMethod]
    public void Register_ExplicitKey()
    {
        var resolver = new MessageTypeResolver();
        resolver.Register<TestMessage>("custom.key");

        var result = resolver.Resolve("custom.key");
        Assert.AreEqual(typeof(TestMessage), result);

        // Default key should not be registered
        Assert.IsNull(resolver.Resolve(typeof(TestMessage).FullName!));
    }

    [TestMethod]
    public void Resolve_CaseInsensitive()
    {
        var resolver = new MessageTypeResolver();
        resolver.Register<TestMessage>("My.Message");

        Assert.AreEqual(typeof(TestMessage), resolver.Resolve("my.message"));
        Assert.AreEqual(typeof(TestMessage), resolver.Resolve("MY.MESSAGE"));
        Assert.AreEqual(typeof(TestMessage), resolver.Resolve("My.Message"));
    }

    [TestMethod]
    public void Resolve_UnknownType_ReturnsNull()
    {
        var resolver = new MessageTypeResolver();

        Assert.IsNull(resolver.Resolve("does.not.exist"));
    }

    [TestMethod]
    public void Register_MultipleTypes()
    {
        var resolver = new MessageTypeResolver();
        resolver.Register<TestMessage>();
        resolver.Register<AnotherMessage>();

        Assert.AreEqual(typeof(TestMessage), resolver.Resolve(typeof(TestMessage).FullName!));
        Assert.AreEqual(typeof(AnotherMessage), resolver.Resolve(typeof(AnotherMessage).FullName!));
    }

    public record TestMessage(string Value);
    public record AnotherMessage(int Number);
}
