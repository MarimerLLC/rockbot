namespace RockBot.Host.Tests;

/// <summary>
/// Pins the resolution rule for the reasoning-scaffolding toggle: an explicit argument to
/// <c>AgentLoopRunner.RunAsync</c> wins, and null falls back to
/// <see cref="AgentHostOptions.EnableReasoningScaffolding"/>. This mirrors the
/// <c>enableReasoningScaffolding ?? hostOptions.Value.EnableReasoningScaffolding</c>
/// expression in the runner, so a change to that precedence breaks a test rather than
/// silently re-enabling task framing for conversational agents.
/// </summary>
[TestClass]
public class AgentHostOptionsScaffoldingTests
{
    private static bool Resolve(bool? explicitValue, AgentHostOptions options) =>
        explicitValue ?? options.EnableReasoningScaffolding;

    [TestMethod]
    public void EnableReasoningScaffolding_DefaultsToTrue()
    {
        // Task-completing agents are the default shape; changing this would silently
        // strip step-by-step guidance from every existing deployment.
        Assert.IsTrue(new AgentHostOptions().EnableReasoningScaffolding);
    }

    [TestMethod]
    public void NullArgument_UsesConfiguredDefault_WhenEnabled()
    {
        var options = new AgentHostOptions { EnableReasoningScaffolding = true };
        Assert.IsTrue(Resolve(null, options));
    }

    [TestMethod]
    public void NullArgument_UsesConfiguredDefault_WhenDisabled()
    {
        var options = new AgentHostOptions { EnableReasoningScaffolding = false };
        Assert.IsFalse(Resolve(null, options));
    }

    [TestMethod]
    public void ExplicitFalse_OverridesEnabledConfig()
    {
        // WorkerRunner passes false explicitly and must keep opting out even where
        // the host default leaves scaffolding on.
        var options = new AgentHostOptions { EnableReasoningScaffolding = true };
        Assert.IsFalse(Resolve(false, options));
    }

    [TestMethod]
    public void ExplicitTrue_OverridesDisabledConfig()
    {
        var options = new AgentHostOptions { EnableReasoningScaffolding = false };
        Assert.IsTrue(Resolve(true, options));
    }

    [TestMethod]
    public void CompletionAndFollowUpDefaults_AreOne_AndDisableableWithZero()
    {
        // The conversational-mode config sets both to 0; confirm 0 is a legal value
        // and that the shipped defaults are unchanged.
        var defaults = new AgentHostOptions();
        Assert.AreEqual(1, defaults.MaxCompletionReprompts);
        Assert.AreEqual(1, defaults.MaxFollowUpPasses);

        var conversational = new AgentHostOptions
        {
            MaxCompletionReprompts = 0,
            MaxFollowUpPasses = 0,
        };
        Assert.AreEqual(0, conversational.MaxCompletionReprompts);
        Assert.AreEqual(0, conversational.MaxFollowUpPasses);
    }
}
