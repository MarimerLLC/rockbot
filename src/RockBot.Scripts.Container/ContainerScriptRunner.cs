using System.Diagnostics;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace RockBot.Scripts.Container;

/// <summary>
/// Runs scripts in ephemeral K8s pods and returns the result directly (no message bus).
/// Used by <see cref="ScriptToolExecutor"/> for tool registry integration.
/// </summary>
internal sealed class ContainerScriptRunner(
    IKubernetes kubernetes,
    ContainerScriptOptions options,
    ILogger<ContainerScriptRunner> logger) : IScriptRunner
{
    public async Task<ScriptInvokeResponse> ExecuteAsync(ScriptInvokeRequest request, CancellationToken ct)
    {
        var podName = $"rockbot-script-{request.ToolCallId[..Math.Min(8, request.ToolCallId.Length)]}-{Guid.NewGuid():N}"[..63].TrimEnd('-');

        try
        {
            var pod = BuildPodSpec(podName, request);
            var sw = Stopwatch.StartNew();

            await kubernetes.CoreV1.CreateNamespacedPodAsync(pod, options.Namespace, cancellationToken: ct);

            var completed = await WaitForPodCompletion(podName, request.TimeoutSeconds, ct);
            sw.Stop();

            if (!completed)
            {
                return new ScriptInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    Stderr = $"Pod timed out after {request.TimeoutSeconds}s",
                    ExitCode = -1,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            var logResponse = await kubernetes.CoreV1.ReadNamespacedPodLogAsync(
                podName, options.Namespace, container: "script", cancellationToken: ct);
            var stdout = await new StreamReader(logResponse).ReadToEndAsync(ct);

            var completedPod = await kubernetes.CoreV1.ReadNamespacedPodAsync(
                podName, options.Namespace, cancellationToken: ct);
            var exitCode = completedPod.Status?.ContainerStatuses?
                .FirstOrDefault(c => c.Name == "script")?
                .State?.Terminated?.ExitCode ?? -1;

            string? stderr = null;
            if (exitCode != 0)
            {
                stderr = completedPod.Status?.ContainerStatuses?
                    .FirstOrDefault(c => c.Name == "script")?
                    .State?.Terminated?.Reason;
            }

            return new ScriptInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                Output = stdout,
                Stderr = stderr,
                ExitCode = exitCode,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            try
            {
                await kubernetes.CoreV1.DeleteNamespacedPodAsync(podName, options.Namespace);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to delete script pod {PodName}", podName);
            }
        }
    }

    private V1Pod BuildPodSpec(string podName, ScriptInvokeRequest request)
    {
        var scriptCommand = "";
        if (request.PipPackages is { Count: > 0 })
        {
            scriptCommand += $"pip install --quiet {string.Join(' ', request.PipPackages)} && ";
        }
        scriptCommand += "python -c \"$ROCKBOT_SCRIPT\"";

        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = options.Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = "rockbot-script",
                    ["rockbot.dev/tool-call-id"] = request.ToolCallId
                }
            },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                ActiveDeadlineSeconds = request.TimeoutSeconds,
                AutomountServiceAccountToken = false,
                EnableServiceLinks = false,
                Containers =
                [
                    new V1Container
                    {
                        Name = "script",
                        Image = options.Image,
                        Command = ["sh", "-c", scriptCommand],
                        Env =
                        [
                            new V1EnvVar { Name = "ROCKBOT_SCRIPT", Value = request.Script },
                            new V1EnvVar { Name = "ROCKBOT_INPUT", Value = request.InputData }
                        ],
                        Resources = new V1ResourceRequirements
                        {
                            Limits = new Dictionary<string, ResourceQuantity>
                            {
                                ["cpu"] = new(options.CpuLimit),
                                ["memory"] = new(options.MemoryLimit)
                            }
                        },
                        SecurityContext = new V1SecurityContext
                        {
                            RunAsNonRoot = true,
                            RunAsUser = 1000,
                            AllowPrivilegeEscalation = false,
                            ReadOnlyRootFilesystem = false
                        }
                    }
                ]
            }
        };
    }

    private async Task<bool> WaitForPodCompletion(string podName, int timeoutSeconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 5));

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var pod = await kubernetes.CoreV1.ReadNamespacedPodStatusAsync(
                    podName, options.Namespace, cancellationToken: cts.Token);

                var phase = pod.Status?.Phase;
                if (phase is "Succeeded" or "Failed")
                    return true;

                await Task.Delay(1000, cts.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout
        }

        return false;
    }
}
