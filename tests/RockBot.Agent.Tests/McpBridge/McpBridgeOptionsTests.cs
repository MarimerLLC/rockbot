using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RockBot.Agent.McpBridge;

namespace RockBot.Agent.Tests.McpBridge;

[TestClass]
public class McpBridgeOptionsTests
{
    private static IConfigurationSection Section(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("McpBridge");

    [TestMethod]
    public void ReadElicitationDefaults_ReadsScalarsAsStringsAndListsAsArrays()
    {
        var defaults = McpBridgeOptions.ReadElicitationDefaults(Section(new()
        {
            ["McpBridge:DefaultElicitation:Defaults:mailbox"] = "work",
            ["McpBridge:DefaultElicitation:Defaults:confirm"] = "true",
            ["McpBridge:DefaultElicitation:Defaults:labels:0"] = "a",
            ["McpBridge:DefaultElicitation:Defaults:labels:1"] = "b",
        }));

        Assert.AreEqual("work", defaults["mailbox"].GetString());
        Assert.AreEqual("true", defaults["confirm"].GetString(), "the coordinator reads it as a literal per field");
        CollectionAssert.AreEqual(new[] { "a", "b" },
            defaults["labels"].EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [TestMethod]
    public void DefaultElicitationDefaults_SurviveOptionsBinding()
    {
        // ConfigurationBinder alone yields an empty Dictionary<string, JsonElement>; the
        // PostConfigure step in Program.cs is what makes these reach the bridge.
        var section = Section(new()
        {
            ["McpBridge:DefaultElicitation:Mode"] = "auto",
            ["McpBridge:DefaultElicitation:Defaults:mailbox"] = "work",
        });

        var services = new ServiceCollection();
        services.Configure<McpBridgeOptions>(section);
        services.PostConfigure<McpBridgeOptions>(options =>
            (options.DefaultElicitation ??= new()).Defaults = McpBridgeOptions.ReadElicitationDefaults(section));

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<McpBridgeOptions>>().Value;

        Assert.AreEqual("work", options.DefaultElicitation.Defaults["mailbox"].GetString());
    }
}
