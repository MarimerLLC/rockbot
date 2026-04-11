using RockBot.Scripts.Docker;

namespace RockBot.Scripts.Tests;

[TestClass]
public class DockerScriptOptionsTests
{
    [TestMethod]
    public void Defaults_AreCorrect()
    {
        var options = new DockerScriptOptions();

        Assert.AreEqual("python:3.12-slim", options.Image);
        Assert.AreEqual("500m", options.CpuLimit);
        Assert.AreEqual("256Mi", options.MemoryLimit);
        Assert.AreEqual("bridge", options.NetworkMode);
        Assert.AreEqual("script.result", options.DefaultResultTopic);
    }

    [TestMethod]
    public void GetNanoCpus_MilliCpu()
    {
        var options = new DockerScriptOptions { CpuLimit = "500m" };
        Assert.AreEqual(500_000_000L, options.GetNanoCpus());
    }

    [TestMethod]
    public void GetNanoCpus_WholeCpu()
    {
        var options = new DockerScriptOptions { CpuLimit = "2" };
        Assert.AreEqual(2_000_000_000L, options.GetNanoCpus());
    }

    [TestMethod]
    public void GetMemoryBytes_Mebibytes()
    {
        var options = new DockerScriptOptions { MemoryLimit = "256Mi" };
        Assert.AreEqual(256L * 1024 * 1024, options.GetMemoryBytes());
    }

    [TestMethod]
    public void GetMemoryBytes_Gibibytes()
    {
        var options = new DockerScriptOptions { MemoryLimit = "1Gi" };
        Assert.AreEqual(1024L * 1024 * 1024, options.GetMemoryBytes());
    }

    [TestMethod]
    public void GetMemoryBytes_Kibibytes()
    {
        var options = new DockerScriptOptions { MemoryLimit = "512Ki" };
        Assert.AreEqual(512L * 1024, options.GetMemoryBytes());
    }

    [TestMethod]
    public void GetMemoryBytes_RawBytes()
    {
        var options = new DockerScriptOptions { MemoryLimit = "1048576" };
        Assert.AreEqual(1048576L, options.GetMemoryBytes());
    }
}
