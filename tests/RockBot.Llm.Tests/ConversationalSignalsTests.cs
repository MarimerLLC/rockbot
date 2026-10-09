using Microsoft.VisualStudio.TestTools.UnitTesting;
using RockBot.Llm;

namespace RockBot.Llm.Tests;

[TestClass]
public class ConversationalSignalsTests
{
    [TestMethod]
    [DataRow("thanks")]
    [DataRow("Thank you!")]
    [DataRow("ok")]
    [DataRow("OK.")]
    [DataRow("ok thanks")]
    [DataRow("okay, got it")]
    [DataRow("got it, thanks again")]
    [DataRow("👍")]
    [DataRow("🙏🙏")]
    [DataRow("sounds good")]
    [DataRow("perfect, thanks for the help")]
    [DataRow("hi")]
    [DataRow("Good morning!")]
    [DataRow("that’s great")]
    public void IsTrivialAck_PureAcknowledgements(string text)
    {
        Assert.IsTrue(ConversationalSignals.IsTrivialAck(text), $"\"{text}\" is a pure acknowledgement.");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("ok, create a deck for me to review")]
    [DataRow("thanks — now fix the outline")]
    [DataRow("ok?")]
    [DataRow("yes")]
    [DataRow("sure")]
    [DataRow("go ahead")]
    [DataRow("do it")]
    [DataRow("figure out a way to update the doc")]
    [DataRow("thanks thanks thanks thanks thanks thanks thanks thanks thanks thanks thanks")]
    public void IsTrivialAck_InstructionsQuestionsAndApprovals_AreNot(string text)
    {
        Assert.IsFalse(ConversationalSignals.IsTrivialAck(text), $"\"{text}\" is not a pure acknowledgement.");
    }

    [TestMethod]
    [DataRow("what are the key features of the MCP version 2 spec")]
    [DataRow("How does OAuth 2.1 differ from the previous version?")]
    [DataRow("which API does the new SDK use for uploads")]
    [DataRow("Explain what changed in the latest release of the protocol.")]
    public void IsResearchQuestion_TechnicalSubjects(string text)
    {
        Assert.IsTrue(ConversationalSignals.IsResearchQuestion(text), $"\"{text}\" is a research question.");
    }

    [TestMethod]
    [DataRow("What is the capital of France?")]
    [DataRow("what's the weather?")]
    [DataRow("what are we doing for dinner tonight")]
    [DataRow("Create the MCP slide deck now please")]
    [DataRow("is it OK if we meet at 5 PM")]
    public void IsResearchQuestion_TriviaInstructionsAndChat_AreNot(string text)
    {
        Assert.IsFalse(ConversationalSignals.IsResearchQuestion(text), $"\"{text}\" is not a research question.");
    }
}
