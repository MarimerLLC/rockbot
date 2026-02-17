using Microsoft.Extensions.DependencyInjection;
using RockBot.Telemetry;

namespace RockBot.Telemetry.Tests;

[TestClass]
public class TelemetryRegistrationTests
{
    [TestMethod]
    public void AddRockBotTelemetry_RegistersOptions()
    {
        var services = new ServiceCollection();
        services.AddRockBotTelemetry(opts =>
        {
            opts.ServiceName = "test-service";
            opts.OtlpEndpoint = "http://collector:4317";
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetService<TelemetryOptions>();

        Assert.IsNotNull(options);
        Assert.AreEqual("test-service", options.ServiceName);
        Assert.AreEqual("http://collector:4317", options.OtlpEndpoint);
    }

    [TestMethod]
    public void AddRockBotTelemetry_DefaultOptions_Work()
    {
        var services = new ServiceCollection();
        services.AddRockBotTelemetry();

        var provider = services.BuildServiceProvider();
        var options = provider.GetService<TelemetryOptions>();

        Assert.IsNotNull(options);
        Assert.AreEqual("rockbot", options.ServiceName);
        Assert.AreEqual("http://localhost:4317", options.OtlpEndpoint);
        Assert.IsTrue(options.EnableTracing);
        Assert.IsTrue(options.EnableMetrics);
    }

    [TestMethod]
    public void AddRockBotTelemetry_TracingDisabled_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddRockBotTelemetry(opts =>
        {
            opts.EnableTracing = false;
            opts.EnableMetrics = false;
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetService<TelemetryOptions>();

        Assert.IsNotNull(options);
        Assert.IsFalse(options.EnableTracing);
        Assert.IsFalse(options.EnableMetrics);
    }
}
