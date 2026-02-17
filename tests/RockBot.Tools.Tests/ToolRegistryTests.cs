using RockBot.Tools;

namespace RockBot.Tools.Tests;

[TestClass]
public class ToolRegistryTests
{
    private readonly ToolRegistry _registry = new();

    [TestMethod]
    public void Register_AddsToolToRegistry()
    {
        var executor = new StubToolExecutor();
        var registration = new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        };

        _registry.Register(registration, executor);

        var tools = _registry.GetTools();
        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("test_tool", tools[0].Name);
    }

    [TestMethod]
    public void GetExecutor_ReturnsRegisteredExecutor()
    {
        var executor = new StubToolExecutor();
        var registration = new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        };

        _registry.Register(registration, executor);

        var result = _registry.GetExecutor("test_tool");
        Assert.AreSame(executor, result);
    }

    [TestMethod]
    public void GetExecutor_ReturnsNullForUnknownTool()
    {
        var result = _registry.GetExecutor("nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Register_ThrowsOnDuplicate()
    {
        var executor = new StubToolExecutor();
        var registration = new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        };

        _registry.Register(registration, executor);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            _registry.Register(registration, executor));
    }

    [TestMethod]
    public void GetTools_ReturnsAllRegisteredTools()
    {
        var executor = new StubToolExecutor();

        _registry.Register(new ToolRegistration
        {
            Name = "tool_a",
            Description = "Tool A",
            Source = "test"
        }, executor);

        _registry.Register(new ToolRegistration
        {
            Name = "tool_b",
            Description = "Tool B",
            Source = "test"
        }, executor);

        var tools = _registry.GetTools();
        Assert.AreEqual(2, tools.Count);
        Assert.IsTrue(tools.Any(t => t.Name == "tool_a"));
        Assert.IsTrue(tools.Any(t => t.Name == "tool_b"));
    }

    [TestMethod]
    public void Register_ThrowsOnNullRegistration()
    {
        var executor = new StubToolExecutor();
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            _registry.Register(null!, executor));
    }

    [TestMethod]
    public void Register_ThrowsOnNullExecutor()
    {
        var registration = new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        };

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            _registry.Register(registration, null!));
    }
}
