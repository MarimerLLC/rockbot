using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Scripts;
using RockBot.Scripts.Container;

namespace RockBot.Scripts.Tests;

/// <summary>
/// End-to-end integration tests that spin up real K8s pods.
/// Gated by the ROCKBOT_K8S_CONTEXT environment variable.
/// Set it to any value (e.g. "docker-desktop" or "k3s") to enable.
///
/// Run with:
///   ROCKBOT_K8S_CONTEXT=docker-desktop dotnet test --filter "ClassName~KubernetesIntegrationTests"
/// </summary>
[TestClass]
public class KubernetesIntegrationTests
{
    private static IKubernetes? _kubernetes;
    private static ContainerScriptOptions _options = new()
    {
        Namespace = "rockbot-scripts",
        Image = "python:3.12-slim",
        CpuLimit = "500m",
        MemoryLimit = "256Mi"
    };

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        var context = Environment.GetEnvironmentVariable("ROCKBOT_K8S_CONTEXT");
        if (string.IsNullOrEmpty(context))
            return;

        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        _kubernetes = new Kubernetes(config);
    }

    private IScriptRunner CreateRunner()
    {
        if (_kubernetes is null)
            Assert.Inconclusive("ROCKBOT_K8S_CONTEXT not set — skipping K8s integration tests");

        return new ContainerScriptRunner(_kubernetes, _options, NullLogger<ContainerScriptRunner>.Instance);
    }

    [TestMethod]
    public async Task ExecuteAsync_SimpleScript_ReturnsStdout()
    {
        var runner = CreateRunner();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "test-simple",
            Script = "print('hello from k8s')"
        };

        var response = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(0, response.ExitCode);
        Assert.IsTrue(response.IsSuccess);
        StringAssert.Contains(response.Output, "hello from k8s");
        Assert.IsTrue(response.ElapsedMs > 0);
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptWithJsonOutput_ReturnsJson()
    {
        var runner = CreateRunner();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "test-json",
            Script = "import json; print(json.dumps({'result': 42, 'ok': True}))"
        };

        var response = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(0, response.ExitCode);
        StringAssert.Contains(response.Output, "\"result\"");
        StringAssert.Contains(response.Output, "42");
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptWithNonZeroExit_ReturnsFailure()
    {
        var runner = CreateRunner();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "test-error",
            Script = "raise ValueError('intentional error')"
        };

        var response = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.AreNotEqual(0, response.ExitCode);
        Assert.IsFalse(response.IsSuccess);
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptWithInputData_ReadsFromEnvVar()
    {
        var runner = CreateRunner();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "test-input",
            Script = "import os; print(os.environ['ROCKBOT_INPUT'])",
            InputData = "hello-input"
        };

        var response = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(0, response.ExitCode);
        StringAssert.Contains(response.Output, "hello-input");
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptWithPipPackage_InstallsAndRuns()
    {
        var runner = CreateRunner();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "test-pip",
            Script = "import requests; print(requests.__version__)",
            PipPackages = ["requests"],
            TimeoutSeconds = 120
        };

        var response = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(0, response.ExitCode);
        Assert.IsNotNull(response.Output);
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptTimeout_ReturnsTimeoutError()
    {
        var runner = CreateRunner();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "test-timeout",
            Script = "import time; time.sleep(300)",
            TimeoutSeconds = 5
        };

        var response = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.AreNotEqual(0, response.ExitCode);
        Assert.IsFalse(response.IsSuccess);
    }
}
