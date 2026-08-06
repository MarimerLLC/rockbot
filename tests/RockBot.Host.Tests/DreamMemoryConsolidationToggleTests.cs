using Microsoft.Extensions.Configuration;

namespace RockBot.Host.Tests;

/// <summary>
/// Guards <see cref="DreamOptions.MemoryConsolidationEnabled"/>. The toggle exists so an
/// agent can keep mining memories while refusing to let the dream model rewrite them —
/// consolidation is the only pass that edits stored entries, and therefore the only one that
/// can introduce detail no source entry contained.
/// </summary>
[TestClass]
public class DreamMemoryConsolidationToggleTests
{
    private static DreamOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var opts = new DreamOptions();
        config.GetSection("Dream").Bind(opts);
        return opts;
    }

    [TestMethod]
    public void DefaultsToEnabled_SoExistingDeploymentsAreUnaffected()
    {
        Assert.IsTrue(new DreamOptions().MemoryConsolidationEnabled);
    }

    [TestMethod]
    public void Binds_FromEnvironmentStyleConfig()
    {
        // Dream__MemoryConsolidationEnabled=false in an env file arrives in this shape.
        var opts = Bind(new Dictionary<string, string?>
        {
            ["Dream:MemoryConsolidationEnabled"] = "false",
        });

        Assert.IsFalse(opts.MemoryConsolidationEnabled);
    }

    [TestMethod]
    public void IsIndependentOfMemoryMining()
    {
        // The point of the toggle: mining keeps writing new entries while consolidation
        // stops rewriting them.
        var opts = Bind(new Dictionary<string, string?>
        {
            ["Dream:MemoryConsolidationEnabled"] = "false",
            ["Dream:MemoryMiningEnabled"] = "true",
        });

        Assert.IsFalse(opts.MemoryConsolidationEnabled);
        Assert.IsTrue(opts.MemoryMiningEnabled);
    }
}
