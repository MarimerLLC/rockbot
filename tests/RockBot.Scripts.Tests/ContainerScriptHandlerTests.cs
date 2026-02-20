using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Scripts;
using RockBot.Scripts.Container;

namespace RockBot.Scripts.Tests;

[TestClass]
public class ContainerScriptHandlerTests
{
    private readonly ContainerScriptOptions _options = new()
    {
        Namespace = "test-ns",
        Image = "python:3.12-slim",
        CpuLimit = "500m",
        MemoryLimit = "256Mi"
    };

    [TestMethod]
    public void BuildPodSpec_SetsCorrectMetadata()
    {
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_123",
            Script = "print('hello')"
        };

        var pod = handler.BuildPodSpec("test-pod", request);

        Assert.AreEqual("test-pod", pod.Metadata.Name);
        Assert.AreEqual("test-ns", pod.Metadata.NamespaceProperty);
        Assert.AreEqual("rockbot-script", pod.Metadata.Labels["app"]);
        Assert.AreEqual("call_123", pod.Metadata.Labels["rockbot.dev/tool-call-id"]);
    }

    [TestMethod]
    public void BuildPodSpec_SetsSecurityContext()
    {
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "print('hello')"
        };

        var pod = handler.BuildPodSpec("test-pod", request);
        var container = pod.Spec.Containers[0];

        Assert.IsTrue(container.SecurityContext.RunAsNonRoot);
        Assert.AreEqual(1000L, container.SecurityContext.RunAsUser);
        Assert.IsFalse(container.SecurityContext.AllowPrivilegeEscalation);
    }

    [TestMethod]
    public void BuildPodSpec_SetsResourceLimits()
    {
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "print('hello')"
        };

        var pod = handler.BuildPodSpec("test-pod", request);
        var limits = pod.Spec.Containers[0].Resources.Limits;

        Assert.IsTrue(limits.ContainsKey("cpu"));
        Assert.IsTrue(limits.ContainsKey("memory"));
    }

    [TestMethod]
    public void BuildPodSpec_SetsNeverRestartPolicy()
    {
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "print('hello')"
        };

        var pod = handler.BuildPodSpec("test-pod", request);

        Assert.AreEqual("Never", pod.Spec.RestartPolicy);
    }

    [TestMethod]
    public void BuildPodSpec_SetsActiveDeadlineFromTimeout()
    {
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "print('hello')",
            TimeoutSeconds = 60
        };

        var pod = handler.BuildPodSpec("test-pod", request);

        Assert.AreEqual(60L, pod.Spec.ActiveDeadlineSeconds);
    }

    [TestMethod]
    public void BuildPodSpec_DisablesServiceAccountMount()
    {
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "print('hello')"
        };

        var pod = handler.BuildPodSpec("test-pod", request);

        Assert.IsFalse(pod.Spec.AutomountServiceAccountToken);
    }

    [TestMethod]
    public void BuildPodSpec_SetsScriptEnvironmentVariables()
    {
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "import json; print(json.dumps({'key': 'value'}))",
            InputData = "some input"
        };

        var pod = handler.BuildPodSpec("test-pod", request);
        var env = pod.Spec.Containers[0].Env;

        Assert.AreEqual("ROCKBOT_SCRIPT", env[0].Name);
        Assert.AreEqual(request.Script, env[0].Value);
        Assert.AreEqual("ROCKBOT_INPUT", env[1].Name);
        Assert.AreEqual("some input", env[1].Value);
    }

    [TestMethod]
    public void BuildPodSpec_IncludesPipInstall_WhenPackagesSpecified()
    {
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "import requests",
            PipPackages = ["requests", "beautifulsoup4"]
        };

        var pod = handler.BuildPodSpec("test-pod", request);
        var command = pod.Spec.Containers[0].Command;

        // sh -c "pip install --target /tmp/pypackages ... && PYTHONPATH=... python -c ..."
        Assert.AreEqual("sh", command[0]);
        Assert.AreEqual("-c", command[1]);
        Assert.IsTrue(command[2].Contains("pip install --quiet --target /tmp/pypackages requests beautifulsoup4"));
        Assert.IsTrue(command[2].Contains("PYTHONPATH=/tmp/pypackages"));
    }

    [TestMethod]
    public void BuildPodSpec_UsesConfiguredImage()
    {
        _options.Image = "python:3.11-alpine";
        var handler = CreateHandler();

        var request = new ScriptInvokeRequest
        {
            ToolCallId = "call_1",
            Script = "print('hello')"
        };

        var pod = handler.BuildPodSpec("test-pod", request);

        Assert.AreEqual("python:3.11-alpine", pod.Spec.Containers[0].Image);
    }

    // BuildPodSpec doesn't use the IKubernetes client, so we pass null.
    // Integration tests that exercise actual K8s API calls are gated by ROCKBOT_K8S_CONTEXT env var.
    private ContainerScriptHandler CreateHandler()
    {
        var publisher = new TrackingPublisher();
        var agent = new AgentIdentity("test-script-agent");
        var logger = NullLogger<ContainerScriptHandler>.Instance;
        return new ContainerScriptHandler(null!, publisher, _options, agent, logger);
    }
}

/// <summary>
/// Captures published envelopes for assertion.
/// </summary>
internal sealed class TrackingPublisher : IMessagePublisher
{
    public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

    public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Published.Add((topic, envelope));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
