namespace RockBot.Host.Tests;

[TestClass]
public class IsErrorResultTests
{
    [TestMethod]
    public void StartsWithErrorPrefix_DetectedAsError() =>
        Assert.IsTrue(RockBotFunctionInvokingChatClient.IsErrorResult(
            "Error: Graph API error: The resource could not be found."));

    [TestMethod]
    public void PlainContent_NotAnError() =>
        Assert.IsFalse(RockBotFunctionInvokingChatClient.IsErrorResult(
            """{"server":"onedrive-personal","files":[]}"""));

    [TestMethod]
    public void ContentMentioningErrorWord_NotDetected() =>
        Assert.IsFalse(RockBotFunctionInvokingChatClient.IsErrorResult(
            "The user's last login attempt produced an Error: condition that the docs explain."),
            "Only the literal 'Error: ' prefix counts — substring matches would mis-flag normal content.");

    [TestMethod]
    public void LowercasePrefix_NotDetected() =>
        Assert.IsFalse(RockBotFunctionInvokingChatClient.IsErrorResult("error: lowercase"),
            "RegistryToolFunction emits 'Error: ' with capital E; matching is case-sensitive.");

    [TestMethod]
    public void ErrorWithoutColon_NotDetected() =>
        Assert.IsFalse(RockBotFunctionInvokingChatClient.IsErrorResult("Error happened, see below"),
            "Without the colon-space, this is just narrative text.");

    [TestMethod]
    public void EmptyString_NotAnError() =>
        Assert.IsFalse(RockBotFunctionInvokingChatClient.IsErrorResult(""));

    [TestMethod]
    public void DoubleErrorPrefix_StillDetected() =>
        Assert.IsTrue(RockBotFunctionInvokingChatClient.IsErrorResult("Error: Error: nested"),
            "Defensive: if the executor's content already started with 'Error', the doubled prefix still counts.");
}
