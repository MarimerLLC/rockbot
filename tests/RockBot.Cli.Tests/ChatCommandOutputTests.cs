using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using RockBot.UserProxy.Cli;

namespace RockBot.Cli.Tests;

/// <summary>Tests for <c>rockbot chat --output</c> (#797).</summary>
[TestClass]
public sealed class ChatCommandOutputTests
{
    [TestMethod]
    public async Task JsonLines_WithoutMessage_FailsBeforeBuildingAHost()
    {
        // The stream describes one turn; the REPL has many. Like --attach, this is checked before
        // HostFactory.Build, so it needs no RabbitMQ.
        var settings = new ChatCommand.Settings { Output = "jsonl" };

        var exitCode = await new ChatCommand().ExecuteAsync(TestCommandContext.Create(), settings);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("jsonl")]
    [DataRow("JSONL")]
    public void KnownFormats_Validate(string format)
    {
        Assert.IsTrue(new ChatCommand.Settings { Output = format }.Validate().Successful);
    }

    [TestMethod]
    public void UnknownFormat_FailsValidation()
    {
        var result = new ChatCommand.Settings { Output = "json" }.Validate();

        Assert.IsFalse(result.Successful);
        StringAssert.Contains(result.Message, "jsonl");
    }

    [TestMethod]
    public void DefaultOutput_IsText()
    {
        var settings = new ChatCommand.Settings();

        Assert.AreEqual("text", settings.Output);
        Assert.IsFalse(settings.JsonLines);
    }

    [TestMethod]
    public void SessionAndUser_DefaultInOnePlace()
    {
        var defaults = new ChatCommand.Settings();
        Assert.AreEqual(CommonSettings.DefaultSessionId, defaults.EffectiveSessionId);
        Assert.AreEqual(CommonSettings.DefaultUserId, defaults.EffectiveUserId);

        var given = new ChatCommand.Settings { SessionId = "s1", UserId = "u1" };
        Assert.AreEqual("s1", given.EffectiveSessionId);
        Assert.AreEqual("u1", given.EffectiveUserId);
    }

    [TestMethod]
    public void EveryHost_LogsToStderr()
    {
        // Stdout carries each command's result (a reply, the JSON-lines stream); a warning logged
        // there would land in whatever the caller captured. Building the host connects nothing.
        using var host = HostFactory.Build(new CommonSettings.Plain(), useRichFrontend: false);

        var options = host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;

        Assert.AreEqual(LogLevel.Trace, options.LogToStandardErrorThreshold);
    }
}
