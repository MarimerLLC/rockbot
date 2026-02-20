using System.Diagnostics;
using RockBot.Scripts.Local;

namespace RockBot.Scripts.Tests;

[TestClass]
public class LocalScriptRunnerTests
{
    [TestMethod]
    public void BuildProcessStartInfo_SetsCorrectExecutableAndScript()
    {
        var scriptFile = "/tmp/script.py";
        var workDir = "/tmp/work";

        var psi = LocalScriptRunner.BuildProcessStartInfo("python3", scriptFile, workDir, null, null);

        Assert.AreEqual("python3", psi.FileName);
        StringAssert.Contains(psi.Arguments, scriptFile);
    }

    [TestMethod]
    public void BuildProcessStartInfo_SetsWorkingDirectory()
    {
        var workDir = "/tmp/work";

        var psi = LocalScriptRunner.BuildProcessStartInfo("python3", "/tmp/script.py", workDir, null, null);

        Assert.AreEqual(workDir, psi.WorkingDirectory);
    }

    [TestMethod]
    public void BuildProcessStartInfo_RedirectsOutputAndError()
    {
        var psi = LocalScriptRunner.BuildProcessStartInfo("python3", "/tmp/script.py", "/tmp", null, null);

        Assert.IsTrue(psi.RedirectStandardOutput);
        Assert.IsTrue(psi.RedirectStandardError);
        Assert.IsFalse(psi.UseShellExecute);
    }

    [TestMethod]
    public void BuildProcessStartInfo_SetsRockbotInputEnvVar_WhenProvided()
    {
        var psi = LocalScriptRunner.BuildProcessStartInfo("python3", "/tmp/script.py", "/tmp", null, "test-input");

        Assert.AreEqual("test-input", psi.Environment["ROCKBOT_INPUT"]);
    }

    [TestMethod]
    public void BuildProcessStartInfo_SetsEmptyRockbotInput_WhenNotProvided()
    {
        var psi = LocalScriptRunner.BuildProcessStartInfo("python3", "/tmp/script.py", "/tmp", null, null);

        Assert.AreEqual(string.Empty, psi.Environment["ROCKBOT_INPUT"]);
    }

    [TestMethod]
    public void BuildProcessStartInfo_SetsPythonPath_WhenProvided()
    {
        var packageDir = "/tmp/work/pypackages";

        var psi = LocalScriptRunner.BuildProcessStartInfo("python3", "/tmp/script.py", "/tmp", packageDir, null);

        Assert.AreEqual(packageDir, psi.Environment["PYTHONPATH"]);
    }

    [TestMethod]
    public void BuildProcessStartInfo_DoesNotSetPythonPath_WhenNotProvided()
    {
        var psi = LocalScriptRunner.BuildProcessStartInfo("python3", "/tmp/script.py", "/tmp", null, null);

        Assert.IsFalse(psi.Environment.ContainsKey("PYTHONPATH"));
    }
}

/// <summary>
/// Integration tests for <see cref="LocalScriptRunner"/> that execute real Python scripts.
/// Gated by the ROCKBOT_PYTHON environment variable (set to python/python3 path to enable).
/// </summary>
[TestClass]
public class LocalScriptRunnerIntegrationTests
{
    private static string? _python;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        _python = Environment.GetEnvironmentVariable("ROCKBOT_PYTHON");
    }

    private IScriptRunner CreateRunner()
    {
        if (_python is null)
            Assert.Inconclusive("ROCKBOT_PYTHON not set — skipping local script integration tests");

        var options = new LocalScriptOptions { PythonExecutable = _python! };
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalScriptRunner>.Instance;
        return new LocalScriptRunner(options, logger);
    }

    [TestMethod]
    public async Task ExecuteAsync_SimpleScript_ReturnsStdout()
    {
        var runner = CreateRunner();

        var response = await runner.ExecuteAsync(new ScriptInvokeRequest
        {
            ToolCallId = "local-simple",
            Script = "print('hello from local')"
        }, CancellationToken.None);

        Assert.AreEqual(0, response.ExitCode);
        Assert.IsTrue(response.IsSuccess);
        StringAssert.Contains(response.Output, "hello from local");
        Assert.IsTrue(response.ElapsedMs >= 0);
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptWithNonZeroExit_ReturnsFailure()
    {
        var runner = CreateRunner();

        var response = await runner.ExecuteAsync(new ScriptInvokeRequest
        {
            ToolCallId = "local-error",
            Script = "raise ValueError('intentional error')"
        }, CancellationToken.None);

        Assert.AreNotEqual(0, response.ExitCode);
        Assert.IsFalse(response.IsSuccess);
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptWithInputData_ReadsFromEnvVar()
    {
        var runner = CreateRunner();

        var response = await runner.ExecuteAsync(new ScriptInvokeRequest
        {
            ToolCallId = "local-input",
            Script = "import os; print(os.environ['ROCKBOT_INPUT'])",
            InputData = "hello-input"
        }, CancellationToken.None);

        Assert.AreEqual(0, response.ExitCode);
        StringAssert.Contains(response.Output, "hello-input");
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptTimeout_ReturnsTimeoutError()
    {
        var runner = CreateRunner();

        var response = await runner.ExecuteAsync(new ScriptInvokeRequest
        {
            ToolCallId = "local-timeout",
            Script = "import time; time.sleep(300)",
            TimeoutSeconds = 2
        }, CancellationToken.None);

        Assert.AreNotEqual(0, response.ExitCode);
        Assert.IsFalse(response.IsSuccess);
        StringAssert.Contains(response.Stderr, "timed out");
    }

    [TestMethod]
    public async Task ExecuteAsync_ScriptWithJsonOutput_ReturnsJson()
    {
        var runner = CreateRunner();

        var response = await runner.ExecuteAsync(new ScriptInvokeRequest
        {
            ToolCallId = "local-json",
            Script = "import json; print(json.dumps({'result': 42, 'ok': True}))"
        }, CancellationToken.None);

        Assert.AreEqual(0, response.ExitCode);
        StringAssert.Contains(response.Output, "\"result\"");
        StringAssert.Contains(response.Output, "42");
    }
}
