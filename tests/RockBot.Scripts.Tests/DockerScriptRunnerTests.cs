using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Scripts.Docker;

namespace RockBot.Scripts.Tests;

[TestClass]
public class DockerScriptRunnerTests
{
    private readonly DockerScriptOptions _options = new()
    {
        Image = "python:3.12-slim",
        CpuLimit = "500m",
        MemoryLimit = "256Mi",
        NetworkMode = "bridge"
    };

    [TestMethod]
    public void BuildCreateParameters_SetsImage()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual("python:3.12-slim", p.Image);
    }

    [TestMethod]
    public void BuildCreateParameters_SetsCommand()
    {
        var runner = CreateRunner();
        var request = MakeRequest("print('hello')");

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual("sh", p.Cmd[0]);
        Assert.AreEqual("-c", p.Cmd[1]);
        Assert.IsTrue(p.Cmd[2].Contains("python -c \"$ROCKBOT_SCRIPT\""));
    }

    [TestMethod]
    public void BuildCreateParameters_SetsUser()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual("1000", p.User);
    }

    [TestMethod]
    public void BuildCreateParameters_SetsEnvironmentVariables()
    {
        var runner = CreateRunner();
        var request = MakeRequest("print('hi')", inputData: "test-input");

        var p = runner.BuildCreateParameters(request);

        Assert.IsTrue(p.Env.Contains("ROCKBOT_SCRIPT=print('hi')"));
        Assert.IsTrue(p.Env.Contains("ROCKBOT_INPUT=test-input"));
    }

    [TestMethod]
    public void BuildCreateParameters_SetsLabels()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual("rockbot-script", p.Labels["app"]);
        Assert.AreEqual("call_123", p.Labels["rockbot.dev/tool-call-id"]);
    }

    [TestMethod]
    public void BuildCreateParameters_SetsNetworkMode()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual("bridge", p.HostConfig.NetworkMode);
    }

    [TestMethod]
    public void BuildCreateParameters_SetsReadonlyRootfs()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.IsTrue(p.HostConfig.ReadonlyRootfs);
    }

    [TestMethod]
    public void BuildCreateParameters_SetsTmpfs()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.IsTrue(p.HostConfig.Tmpfs.ContainsKey("/tmp"));
    }

    [TestMethod]
    public void BuildCreateParameters_SetsCpuLimit()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual(500_000_000L, p.HostConfig.NanoCPUs);
    }

    [TestMethod]
    public void BuildCreateParameters_SetsMemoryLimit()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual(256L * 1024 * 1024, p.HostConfig.Memory);
    }

    [TestMethod]
    public void BuildCreateParameters_SetsSecurityOpt()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        CollectionAssert.Contains(p.HostConfig.SecurityOpt.ToList(), "no-new-privileges");
    }

    [TestMethod]
    public void BuildCreateParameters_SetsNoAutoRemove()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.IsFalse(p.HostConfig.AutoRemove);
    }

    [TestMethod]
    public void BuildCreateParameters_SetsNoRestartPolicy()
    {
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual(RestartPolicyKind.No, p.HostConfig.RestartPolicy.Name);
    }

    [TestMethod]
    public void BuildCreateParameters_IncludesPipInstall_WhenPackagesSpecified()
    {
        var runner = CreateRunner();
        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_123",
            Script = "import requests",
            PipPackages = ["requests", "beautifulsoup4"]
        };

        var p = runner.BuildCreateParameters(request);

        Assert.IsTrue(p.Cmd[2].Contains("pip install --quiet --target /tmp/pypackages requests beautifulsoup4"));
        Assert.IsTrue(p.Cmd[2].Contains("PYTHONPATH=/tmp/pypackages"));
    }

    [TestMethod]
    public void BuildCreateParameters_UsesConfiguredImage()
    {
        _options.Image = "python:3.11-alpine";
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.AreEqual("python:3.11-alpine", p.Image);
    }

    [TestMethod]
    public void BuildCreateParameters_MountsSharedVolume_WhenConfigured()
    {
        _options.SharedVolumeName = "rockbot-shared";
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.IsTrue(p.Env.Contains("ROCKBOT_SHARED_PATH=/rockbot/shared"));
        Assert.IsNotNull(p.HostConfig.Binds);
        Assert.IsTrue(p.HostConfig.Binds.Contains("rockbot-shared:/rockbot/shared"));
    }

    [TestMethod]
    public void BuildCreateParameters_NoSharedVolume_WhenEmpty()
    {
        _options.SharedVolumeName = "";
        var runner = CreateRunner();
        var request = MakeRequest();

        var p = runner.BuildCreateParameters(request);

        Assert.IsFalse(p.Env.Any(e => e.StartsWith("ROCKBOT_SHARED_PATH=")));
        Assert.IsNull(p.HostConfig.Binds);
    }

    // BuildCreateParameters doesn't use the IDockerClient, so we pass null.
    private DockerScriptRunner CreateRunner()
    {
        var logger = NullLogger<DockerScriptRunner>.Instance;
        return new DockerScriptRunner(null!, _options, logger);
    }

    private static ScriptInvokeRequest MakeRequest(string script = "print('hello')", string? inputData = null)
    {
        return new ScriptInvokeRequest
        {
            ToolCallId = "call_123",
            Script = script,
            InputData = inputData
        };
    }
}
