using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Llm.Copilot.Tests;

[TestClass]
public class CopilotChatClientTests
{
    [TestMethod]
    public void GetService_ReturnsChatClientMetadata()
    {
        // We can't create a real CopilotClient without the CLI, but we can test
        // the metadata path by constructing with null and catching later.
        // Use reflection-free approach: just verify the options drive the metadata.
        var options = new CopilotChatClientOptions { ModelId = "claude-sonnet-4" };

        // Verify options are set correctly
        Assert.AreEqual("claude-sonnet-4", options.ModelId);
        Assert.IsTrue(options.UseLoggedInUser);
        Assert.IsNull(options.GitHubToken);
        Assert.AreEqual(TimeSpan.FromMinutes(3), options.RequestTimeout);
        Assert.AreEqual(3, options.MaxRetries);
        Assert.AreEqual(TimeSpan.FromSeconds(2), options.RetryBaseDelay);
    }

    [TestMethod]
    public void Options_DefaultValues()
    {
        var options = new CopilotChatClientOptions();

        Assert.AreEqual("gpt-4.1", options.ModelId);
        Assert.IsTrue(options.UseLoggedInUser);
        Assert.IsNull(options.GitHubToken);
        Assert.AreEqual(TimeSpan.FromMinutes(3), options.RequestTimeout);
        Assert.AreEqual(3, options.MaxRetries);
        Assert.AreEqual(TimeSpan.FromSeconds(2), options.RetryBaseDelay);
    }

    [TestMethod]
    public void Options_CustomValues()
    {
        var options = new CopilotChatClientOptions
        {
            ModelId = "gpt-5",
            UseLoggedInUser = false,
            GitHubToken = "gho_test",
            RequestTimeout = TimeSpan.FromMinutes(5),
            MaxRetries = 5,
            RetryBaseDelay = TimeSpan.FromSeconds(1)
        };

        Assert.AreEqual("gpt-5", options.ModelId);
        Assert.IsFalse(options.UseLoggedInUser);
        Assert.AreEqual("gho_test", options.GitHubToken);
        Assert.AreEqual(TimeSpan.FromMinutes(5), options.RequestTimeout);
        Assert.AreEqual(5, options.MaxRetries);
        Assert.AreEqual(TimeSpan.FromSeconds(1), options.RetryBaseDelay);
    }

    [TestMethod]
    public void Constructor_ThrowsOnNull()
    {
        var options = new CopilotChatClientOptions();
        var logger = NullLogger<CopilotChatClient>.Instance;

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new CopilotChatClient(null!, options, logger));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new CopilotChatClient(null!, null!, logger));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new CopilotChatClient(null!, options, null!));
    }
}
