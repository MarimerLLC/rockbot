using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Scripts.Docker;

/// <summary>
/// Handles script invocation requests by creating ephemeral Docker containers.
/// </summary>
internal sealed class DockerScriptHandler(
    IDockerClient docker,
    IMessagePublisher publisher,
    DockerScriptOptions options,
    AgentIdentity agent,
    ILogger<DockerScriptHandler> logger) : IMessageHandler<ScriptInvokeRequest>
{
    public async Task HandleAsync(ScriptInvokeRequest request, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo ?? options.DefaultResultTopic;
        var correlationId = context.Envelope.CorrelationId;
        string? containerId = null;

        try
        {
            var createParams = BuildCreateParameters(request);
            var sw = Stopwatch.StartNew();

            logger.LogDebug("Creating script container for call {ToolCallId}", request.ToolCallId);

            var createResponse = await docker.Containers.CreateContainerAsync(createParams, context.CancellationToken);
            containerId = createResponse.ID;

            await docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), context.CancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds + 5));

            string? stdout = null;
            string? stderr = null;
            int exitCode;

            try
            {
                var waitResponse = await docker.Containers.WaitContainerAsync(containerId, cts.Token);
                sw.Stop();

                (stdout, stderr) = await ReadLogsAsync(containerId, context.CancellationToken);
                exitCode = (int)waitResponse.StatusCode;
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                exitCode = -1;
                stderr = $"Container timed out after {request.TimeoutSeconds}s";
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
            if (containerId is not null)
            {
                try
                {
                    await docker.Containers.RemoveContainerAsync(containerId,
                        new ContainerRemoveParameters { Force = true, RemoveVolumes = true });
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to remove script container {ContainerId}", containerId);
                }
            }
        }
    }

    private CreateContainerParameters BuildCreateParameters(ScriptInvokeRequest request)
    {
        var scriptCommand = "";
        if (request.PipPackages is { Count: > 0 })
        {
            scriptCommand += $"pip install --quiet --target /tmp/pypackages {string.Join(' ', request.PipPackages)} 2>&1 && ";
            scriptCommand += "PYTHONPATH=/tmp/pypackages ";
        }
        scriptCommand += "python -c \"$ROCKBOT_SCRIPT\" 2>&1";

        var env = new List<string>
        {
            $"ROCKBOT_SCRIPT={request.Script}",
            $"ROCKBOT_INPUT={request.InputData}"
        };

        var binds = new List<string>();
        if (!string.IsNullOrEmpty(options.SharedVolumeName))
        {
            env.Add($"ROCKBOT_SHARED_PATH={options.SharedVolumePath}");
            binds.Add($"{options.SharedVolumeName}:{options.SharedVolumePath}");
        }

        return new CreateContainerParameters
        {
            Image = options.Image,
            Cmd = ["sh", "-c", scriptCommand],
            User = "1000",
            Env = env,
            Labels = new Dictionary<string, string>
            {
                ["app"] = "rockbot-script",
                ["rockbot.dev/tool-call-id"] = request.ToolCallId
            },
            HostConfig = new HostConfig
            {
                NetworkMode = options.NetworkMode,
                ReadonlyRootfs = true,
                Tmpfs = new Dictionary<string, string> { ["/tmp"] = "" },
                Binds = binds.Count > 0 ? binds : null,
                NanoCPUs = options.GetNanoCpus(),
                Memory = options.GetMemoryBytes(),
                SecurityOpt = ["no-new-privileges"],
                AutoRemove = false,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
            }
        };
    }

    private async Task<(string stdout, string stderr)> ReadLogsAsync(string containerId, CancellationToken ct)
    {
        var logStream = await docker.Containers.GetContainerLogsAsync(
            containerId,
            tty: false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true },
            ct);

        return await logStream.ReadOutputToEndAsync(ct);
    }
}
