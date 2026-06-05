using Microsoft.Extensions.Configuration;

namespace RockBot.Host.Tests;

/// <summary>
/// Guards the config wiring added so the Helm ConfigMap's <c>Dream__LogRetention*</c>
/// keys actually reach <see cref="DreamOptions"/>. The agent binds the <c>Dream</c>
/// section via <c>GetSection("Dream").Bind(opts)</c>; these tests reproduce that bind
/// against the exact string shapes the ConfigMap emits — in particular the
/// <c>TimeSpan</c> "d.hh:mm:ss" form, which silently falls back to the default if the
/// binder can't parse it.
/// </summary>
[TestClass]
public class DreamOptionsRetentionBindingTests
{
    private static DreamOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var opts = new DreamOptions();
        config.GetSection("Dream").Bind(opts);
        return opts;
    }

    [TestMethod]
    public void Binds_RetentionKnobs_FromConfigMapStringShapes()
    {
        // Exactly what configmap.yaml renders (env vars use "__" as the section separator).
        var opts = Bind(new Dictionary<string, string?>
        {
            ["Dream:LogRetentionEnabled"] = "false",
            ["Dream:LogRetentionMaxFileAge"] = "30.00:00:00",
            ["Dream:LogRetentionMaxFilesPerDirectory"] = "1000",
            ["Dream:LogRetentionMaxLinesPerFile"] = "10000",
        });

        Assert.IsFalse(opts.LogRetentionEnabled);
        Assert.AreEqual(TimeSpan.FromDays(30), opts.LogRetentionMaxFileAge);
        Assert.AreEqual(1000, opts.LogRetentionMaxFilesPerDirectory);
        Assert.AreEqual(10_000, opts.LogRetentionMaxLinesPerFile);
    }

    [TestMethod]
    public void MissingSection_LeavesCodeDefaults()
    {
        var opts = Bind(new Dictionary<string, string?> { ["Other:Key"] = "x" });

        Assert.IsTrue(opts.LogRetentionEnabled);
        Assert.AreEqual(TimeSpan.FromDays(30), opts.LogRetentionMaxFileAge);
        Assert.AreEqual(1000, opts.LogRetentionMaxFilesPerDirectory);
        Assert.AreEqual(50_000, opts.LogRetentionMaxLinesPerFile);
    }

    [TestMethod]
    public void ZeroValues_DisableDimensions_AndBind()
    {
        var opts = Bind(new Dictionary<string, string?>
        {
            ["Dream:LogRetentionMaxFileAge"] = "0",
            ["Dream:LogRetentionMaxFilesPerDirectory"] = "0",
            ["Dream:LogRetentionMaxLinesPerFile"] = "0",
        });

        Assert.AreEqual(TimeSpan.Zero, opts.LogRetentionMaxFileAge);
        Assert.AreEqual(0, opts.LogRetentionMaxFilesPerDirectory);
        Assert.AreEqual(0, opts.LogRetentionMaxLinesPerFile);
    }
}
