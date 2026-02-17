using RockBot.Llm;
using RockBot.Tools;

namespace RockBot.Tools.Tests;

[TestClass]
public class ToolRegistrationTests
{
    [TestMethod]
    public void ToLlmToolDefinition_MapsAllFields()
    {
        var registration = new ToolRegistration
        {
            Name = "get_weather",
            Description = "Gets the weather for a city",
            ParametersSchema = """{"type":"object","properties":{"city":{"type":"string"}}}""",
            Source = "rest"
        };

        var definition = registration.ToLlmToolDefinition();

        Assert.AreEqual("get_weather", definition.Name);
        Assert.AreEqual("Gets the weather for a city", definition.Description);
        Assert.AreEqual("""{"type":"object","properties":{"city":{"type":"string"}}}""", definition.ParametersSchema);
    }

    [TestMethod]
    public void ToLlmToolDefinition_HandlesNullSchema()
    {
        var registration = new ToolRegistration
        {
            Name = "ping",
            Description = "Pings a server",
            Source = "test"
        };

        var definition = registration.ToLlmToolDefinition();

        Assert.AreEqual("ping", definition.Name);
        Assert.IsNull(definition.ParametersSchema);
    }
}
