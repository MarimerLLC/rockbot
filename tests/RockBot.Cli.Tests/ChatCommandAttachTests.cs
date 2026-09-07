using RockBot.UserProxy.Cli;
using Spectre.Console.Cli;

namespace RockBot.Cli.Tests;

/// <summary>
/// Tests for <c>rockbot chat --attach</c> (issue #565). The CLI has no access to the shared
/// volume — it may not even be on the same machine — so a file is uploaded to the agent over the
/// bus and the message carries only the path reference that comes back.
/// </summary>
[TestClass]
public sealed class ChatCommandAttachTests
{
    [TestMethod]
    public async Task Attach_WithoutMessage_FailsBeforeBuildingAHost()
    {
        // Attaching per-message in the REPL needs an affordance it does not have. Rejecting the
        // combination is honest; quietly attaching to whichever message happened to go first
        // would not be. This also runs before HostFactory.Build, so it needs no RabbitMQ — which
        // is the property that makes it testable at all.
        var settings = new ChatCommand.Settings
        {
            Attach = ["some-file.png"],
            RabbitMqHost = "localhost",
            RabbitMqUser = "test",
            RabbitMqPassword = "test",
        };

        var exitCode = await new ChatCommand().ExecuteAsync(Context(), settings);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public void AttachOption_IsRepeatable()
    {
        // Spectre binds a string[] for a repeated option; a single-valued property would
        // silently keep only the last --attach.
        var property = typeof(ChatCommand.Settings).GetProperty(nameof(ChatCommand.Settings.Attach));

        Assert.IsNotNull(property);
        Assert.AreEqual(typeof(string[]), property!.PropertyType);
    }

    [TestMethod]
    public void MimeGuess_CoversTheTypesTheAgentAccepts()
    {
        Assert.AreEqual("image/png", AttachmentMimeGuess.FromFileName("shot.PNG"));
        Assert.AreEqual("image/jpeg", AttachmentMimeGuess.FromFileName("a.jpg"));
        Assert.AreEqual("image/jpeg", AttachmentMimeGuess.FromFileName("a.jpeg"));
        Assert.AreEqual("application/pdf", AttachmentMimeGuess.FromFileName("invoice.pdf"));
    }

    [TestMethod]
    public void MimeGuess_UnknownType_DefersToTheAgentRatherThanInventingOne()
    {
        // The agent owns the allowlist and answers with what it does accept. A guess here would
        // only turn a clear rejection into a confusing one.
        Assert.AreEqual("application/octet-stream", AttachmentMimeGuess.FromFileName("notes.txt"));
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
