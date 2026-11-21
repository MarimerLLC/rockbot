namespace RockBot.Wisp.Tests;

/// <summary>
/// #848: a saved wisp may be switched on as a scheduled task's gate without a person asking only
/// when every step is a direct call that only reads.
/// </summary>
[TestClass]
public sealed class SavedWispRunnerTests
{
    private static WispDefinition Def(params WispStep[] steps) => new() { Description = "gate", Steps = steps };

    private static WispStep Mcp(string id, string tool) =>
        new() { Id = id, Mode = StepMode.Direct, Gateway = GatewayType.Mcp, Server = "mail", Tool = tool };

    [TestMethod]
    public void DirectReads_AreReadOnly()
    {
        var check = SavedWispRunner.InspectDefinition(Def(
            Mcp("search", "search_messages"),
            Mcp("get", "get_message"),
            new WispStep { Id = "page", Mode = StepMode.Direct, Gateway = GatewayType.Web }));

        Assert.IsTrue(check.Valid);
        Assert.IsTrue(check.ReadOnly, check.Reason);
    }

    [TestMethod]
    public void AWrite_AnLlmStep_OrAnAgentCall_IsNotReadOnly()
    {
        var write = SavedWispRunner.InspectDefinition(Def(Mcp("search", "search_messages"), Mcp("send", "send_email")));
        var llm = SavedWispRunner.InspectDefinition(Def(new WispStep { Id = "think", Mode = StepMode.Llm }));
        var agent = SavedWispRunner.InspectDefinition(Def(
            new WispStep { Id = "ask", Mode = StepMode.Direct, Gateway = GatewayType.A2A }));
        var noTool = SavedWispRunner.InspectDefinition(Def(
            new WispStep { Id = "x", Mode = StepMode.Direct, Gateway = GatewayType.Mcp, Server = "mail" }));

        Assert.IsFalse(write.ReadOnly);
        StringAssert.Contains(write.Reason, "'send_email', which writes");
        Assert.IsFalse(llm.ReadOnly);
        StringAssert.Contains(llm.Reason, "LLM step");
        Assert.IsFalse(agent.ReadOnly);
        StringAssert.Contains(agent.Reason, "A2A");
        Assert.IsFalse(noTool.ReadOnly);
        Assert.IsTrue(write.Valid && llm.Valid && agent.Valid && noTool.Valid);
    }

    [TestMethod]
    public void ParseDefinition_ReadsOneWisp_AndRejectsBadJson()
    {
        var def = SpawnWispsExecutor.ParseDefinition("""
            {"description":"new mail","steps":[{"id":"s","mode":"Direct","gateway":"Mcp","server":"mail","tool":"search_messages","params":{"q":"after:2026/11/01"}}]}
            """, out var error);
        var bad = SpawnWispsExecutor.ParseDefinition("{not json", out var badError);

        Assert.IsNotNull(def, error);
        Assert.AreEqual("search_messages", def.Steps.Single().Tool);
        Assert.IsTrue(SavedWispRunner.InspectDefinition(def).ReadOnly);
        Assert.IsNull(bad);
        Assert.IsFalse(string.IsNullOrEmpty(badError));
    }
}
