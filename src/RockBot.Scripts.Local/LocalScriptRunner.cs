using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RockBot.Scripts.Local;

/// <summary>
/// Runs Python scripts as child processes on the local host.
/// Provides process-boundary isolation (separate memory space) without requiring
/// Kubernetes or Docker. See <c>design/script-isolation-alternatives.md</c> for
/// the full isolation trade-off analysis.
/// </summary>
/// <remarks>
/// Security note: Scripts run on the host OS with full access to the host filesystem
/// and network. This runner is intended for development environments and scenarios
/// where the operator trusts the scripts being executed.
/// </remarks>
internal sealed class LocalScriptRunner(
    LocalScriptOptions options,
    ILogger<LocalScriptRunner> logger) : IScriptRunner
{
    public async Task<ScriptInvokeResponse> ExecuteAsync(ScriptInvokeRequest request, CancellationToken ct)
    {
        var workDir = options.WorkingDirectory ?? Path.Combine(Path.GetTempPath(), $"rockbot-script-{Guid.NewGuid():N}");
        var createdWorkDir = options.WorkingDirectory is null;

        try
        {
            if (createdWorkDir)
                Directory.CreateDirectory(workDir);

            var timeout = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : options.DefaultTimeoutSeconds;

            // Install pip packages into a per-execution temp directory so they don't
            // pollute the host environment. Packages are cleaned up with workDir.
            string? pythonPath = null;
            if (request.PipPackages is { Count: > 0 })
            {
                var pipResult = await InstallPipPackagesAsync(request.PipPackages, workDir, timeout, ct);
                if (pipResult.ExitCode != 0)
                {
                    return new ScriptInvokeResponse
                    {
                        ToolCallId = request.ToolCallId,
                        Stderr = $"pip install failed (exit {pipResult.ExitCode}):\n{pipResult.Stderr}",
                        ExitCode = pipResult.ExitCode,
                        ElapsedMs = pipResult.ElapsedMs
                    };
                }

                pythonPath = Path.Combine(workDir, "pypackages");
            }

            return await RunScriptAsync(request, workDir, pythonPath, timeout, ct);
        }
        finally
        {
            if (createdWorkDir)
            {
                try
                {
                    Directory.Delete(workDir, recursive: true);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to delete script working directory {WorkDir}", workDir);
                }
            }
        }
    }

    private async Task<ScriptInvokeResponse> RunScriptAsync(
        ScriptInvokeRequest request,
        string workDir,
        string? pythonPath,
        int timeoutSeconds,
        CancellationToken ct)
    {
        // Write the script to a temp file so there are no shell-quoting issues
        // with the -c flag for multi-line or quote-heavy scripts.
        var scriptFile = Path.Combine(workDir, "script.py");
        await File.WriteAllTextAsync(scriptFile, request.Script, Encoding.UTF8, ct);

        var psi = BuildProcessStartInfo(options.PythonExecutable, scriptFile, workDir, pythonPath, request.InputData);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            sw.Stop();

            return new ScriptInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                Output = stdoutBuilder.Length > 0 ? stdoutBuilder.ToString() : null,
                Stderr = stderrBuilder.Length > 0 ? stderrBuilder.ToString() : null,
                ExitCode = process.ExitCode,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            KillProcess(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Script timeout — kill and report
            KillProcess(process);
            sw.Stop();

            return new ScriptInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                Stderr = $"Script timed out after {timeoutSeconds}s",
                ExitCode = -1,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<(int ExitCode, string? Stderr, long ElapsedMs)> InstallPipPackagesAsync(
        IReadOnlyList<string> packages,
        string workDir,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var packageDir = Path.Combine(workDir, "pypackages");
        Directory.CreateDirectory(packageDir);

        var packageList = string.Join(" ", packages);
        var psi = new ProcessStartInfo(options.PythonExecutable,
            $"-m pip install --quiet --target \"{packageDir}\" {packageList}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        var stderrBuilder = new StringBuilder();

        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            sw.Stop();
            return (process.ExitCode, stderrBuilder.Length > 0 ? stderrBuilder.ToString() : null, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            KillProcess(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            sw.Stop();
            return (-1, $"pip install timed out after {timeoutSeconds}s", sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> for running a Python script file.
    /// Separated for unit testability.
    /// </summary>
    internal static ProcessStartInfo BuildProcessStartInfo(
        string pythonExecutable,
        string scriptFile,
        string workDir,
        string? pythonPath,
        string? inputData)
    {
        var psi = new ProcessStartInfo(pythonExecutable, $"\"{scriptFile}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir
        };

        psi.Environment["ROCKBOT_INPUT"] = inputData ?? string.Empty;

        if (pythonPath is not null)
            psi.Environment["PYTHONPATH"] = pythonPath;

        return psi;
    }

    private static void KillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited
        }
    }
}
