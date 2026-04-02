using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace RockBot.Scripts.Docker;

/// <summary>
/// Runs scripts in ephemeral Docker containers and returns the result directly (no message bus).
/// </summary>
internal sealed class DockerScriptRunner(
    IDockerClient docker,
    DockerScriptOptions options,
    ILogger<DockerScriptRunner> logger) : IScriptRunner
{
    public async Task<ScriptInvokeResponse> ExecuteAsync(ScriptInvokeRequest request, CancellationToken ct)
    {
        string? containerId = null;

        try
        {
            var createParams = BuildCreateParameters(request);
            var sw = Stopwatch.StartNew();

            logger.LogDebug("Creating script container for call {ToolCallId}", request.ToolCallId);

            var createResponse = await docker.Containers.CreateContainerAsync(createParams, ct);
            containerId = createResponse.ID;

            await docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds + 5));

            ContainerWaitResponse waitResponse;
            try
            {
                waitResponse = await docker.Containers.WaitContainerAsync(containerId, cts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new ScriptInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    Stderr = $"Container timed out after {request.TimeoutSeconds}s",
                    ExitCode = -1,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            sw.Stop();

            var (stdout, stderr) = await ReadLogsAsync(containerId, ct);

            return new ScriptInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                Output = stdout,
                Stderr = stderr,
                ExitCode = (int)waitResponse.StatusCode,
                ElapsedMs = sw.ElapsedMilliseconds
            };
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

    internal CreateContainerParameters BuildCreateParameters(ScriptInvokeRequest request)
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

        if (!string.IsNullOrEmpty(options.StagingUrl))
            env.Add($"ROCKBOT_STAGING_URL={options.StagingUrl}");
        if (!string.IsNullOrEmpty(options.StagingToken))
            env.Add($"ROCKBOT_STAGING_TOKEN={options.StagingToken}");

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
