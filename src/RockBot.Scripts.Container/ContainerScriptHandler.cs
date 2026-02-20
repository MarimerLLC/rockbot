using System.Diagnostics;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Scripts.Container;

/// <summary>
/// Handles script invocation requests by creating ephemeral K8s pods.
/// </summary>
internal sealed class ContainerScriptHandler(
    IKubernetes kubernetes,
    IMessagePublisher publisher,
    ContainerScriptOptions options,
    AgentIdentity agent,
    ILogger<ContainerScriptHandler> logger) : IMessageHandler<ScriptInvokeRequest>
{
    public async Task HandleAsync(ScriptInvokeRequest request, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? options.DefaultResultTopic;
        var correlationId = context.Envelope.CorrelationId;
        var podNameRaw = $"rockbot-script-{request.ToolCallId[..Math.Min(8, request.ToolCallId.Length)]}-{Guid.NewGuid():N}";
        var podName = podNameRaw[..Math.Min(63, podNameRaw.Length)].TrimEnd('-');

        try
        {
            var pod = BuildPodSpec(podName, request);
            var sw = Stopwatch.StartNew();

            logger.LogDebug("Creating script pod {PodName} for call {ToolCallId}", podName, request.ToolCallId);

            await kubernetes.CoreV1.CreateNamespacedPodAsync(pod, options.Namespace, cancellationToken: context.CancellationToken);

            var completed = await WaitForPodCompletion(podName, request.TimeoutSeconds, context.CancellationToken);
            sw.Stop();

            string? stdout = null;
            string? stderr = null;
            int exitCode;

            if (completed)
            {
                var logResponse = await kubernetes.CoreV1.ReadNamespacedPodLogAsync(
                    podName, options.Namespace, container: "script", cancellationToken: context.CancellationToken);
                stdout = await new StreamReader(logResponse).ReadToEndAsync(context.CancellationToken);

                var completedPod = await kubernetes.CoreV1.ReadNamespacedPodAsync(
                    podName, options.Namespace, cancellationToken: context.CancellationToken);
                exitCode = completedPod.Status?.ContainerStatuses?
                    .FirstOrDefault(c => c.Name == "script")?
                    .State?.Terminated?.ExitCode ?? -1;
            }
            else
            {
                exitCode = -1;
                stderr = $"Pod timed out after {request.TimeoutSeconds}s";
            }

            var response = new ScriptInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                Output = stdout,
                Stderr = stderr,
                ExitCode = exitCode,
                ElapsedMs = sw.ElapsedMilliseconds
            };

            var envelope = response.ToEnvelope<ScriptInvokeResponse>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Script execution failed for call {ToolCallId}", request.ToolCallId);

            var response = new ScriptInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                Stderr = ex.Message,
                ExitCode = -1,
                ElapsedMs = 0
            };

            var envelope = response.ToEnvelope<ScriptInvokeResponse>(
                source: agent.Name,
                correlationId: correlationId);

            await publisher.PublishAsync(replyTo, envelope, context.CancellationToken);
        }
        finally
        {
            // Best-effort cleanup
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

    internal V1Pod BuildPodSpec(string podName, ScriptInvokeRequest request)
    {
        var command = new List<string> { "sh", "-c" };

        var scriptCommand = "";
        if (request.PipPackages is { Count: > 0 })
        {
            scriptCommand += $"pip install --quiet --target /tmp/pypackages {string.Join(' ', request.PipPackages)} 2>&1 && ";
            scriptCommand += "PYTHONPATH=/tmp/pypackages ";
        }
        scriptCommand += "python -c \"$ROCKBOT_SCRIPT\" 2>&1";

        command.Add(scriptCommand);

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
                        Command = command,
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
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 5)); // Extra buffer beyond pod deadline

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
            // Timeout exceeded
        }

        return false;
    }
}
