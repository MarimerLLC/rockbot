using RockBot.UserProxy.Cli;
using Spectre.Console.Cli;

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

        var exitCode = await new ChatCommand().ExecuteAsync(Context(), settings);

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

    private static CommandContext Context() =>
        new([], new NoRemainingArguments(), name: "chat", data: null);

    private sealed class NoRemainingArguments : IRemainingArguments
    {
        public ILookup<string, string?> Parsed { get; } =
            Array.Empty<KeyValuePair<string, string?>>().ToLookup(p => p.Key, p => p.Value);

        public IReadOnlyList<string> Raw { get; } = [];
    }
}
