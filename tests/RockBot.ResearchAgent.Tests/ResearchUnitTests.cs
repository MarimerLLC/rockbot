using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol.Protocol;

namespace RockBot.ResearchAgent.Tests;

[TestClass]
public class ResearchScoperParseTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not json at all")]
    [DataRow("{\"ambiguous\": tru")]
    [DataRow("{\"ambiguous\": false, \"interpretations\": [], \"best\": null}")]
    [DataRow("{\"ambiguous\": true, \"interpretations\": [{\"id\":\"only\",\"label\":\"Only one\"}], \"best\": \"only\"}")]
    [DataRow("{\"ambiguous\": true, \"interpretations\": [{\"id\":\"a\",\"label\":\"\"},{\"id\":\"b\",\"label\":\"B\"}]}")]
    public void AnythingButTwoOrMoreReadings_IsClear(string? reply) =>
        Assert.IsFalse(ResearchScoper.Parse(reply).IsAmbiguous);

    [TestMethod]
    public void AnAmbiguousReply_GivesItsReadings_AndBest()
    {
        var scope = ResearchScoper.Parse("""
            ```json
            {"ambiguous": true,
             "interpretations": [{"id":"planet","label":"The planet"},{"id":"element","label":"The element"}],
             "best": "element"}
            ```
            """);

        Assert.IsTrue(scope.IsAmbiguous);
        CollectionAssert.AreEqual(new[] { "planet", "element" }, scope.Interpretations.Select(i => i.Id).ToArray());
        Assert.AreEqual("element", scope.Best!.Id);
    }

    [TestMethod]
    public void AnUnknownBest_FallsBackToTheFirstReading_AndDuplicatesAndExtrasAreDropped()
    {
        var items = string.Join(",", Enumerable.Range(0, 8).Select(i => $"{{\"id\":\"o{i % 7}\",\"label\":\"L{i}\"}}"));
        var scope = ResearchScoper.Parse($"{{\"ambiguous\":true,\"interpretations\":[{items}],\"best\":\"nope\"}}");

        Assert.AreEqual(ResearchScoper.MaxInterpretations, scope.Interpretations.Count);
        Assert.AreEqual("o0", scope.Best!.Id);
        Assert.AreEqual(scope.Interpretations.Count, scope.Interpretations.Select(i => i.Id).Distinct().Count());
    }

    [TestMethod]
    public void MissingIds_AreGenerated()
    {
        var scope = ResearchScoper.Parse("{\"ambiguous\":true,\"interpretations\":[{\"label\":\"A\"},{\"label\":\"B\"}]}");

        CollectionAssert.AreEqual(new[] { "option-1", "option-2" }, scope.Interpretations.Select(i => i.Id).ToArray());
    }
}

[TestClass]
public class ResearchScopeTokenTests
{
    private static readonly ResearchScope Mercury = new(
        [new("planet", "The planet"), new("element", "The element")], "element");

    private static ResearchScopeToken Token(TimeSpan? lifetime = null, IDataProtectionProvider? provider = null) =>
        new(provider ?? new EphemeralDataProtectionProvider(), lifetime ?? TimeSpan.FromMinutes(10));

    [TestMethod]
    public void ARoundTrip_GivesTheScopeBack()
    {
        var token = Token();
        var request = new ResearchRequest("Mercury", "ctx", "since 2020");

        var scope = token.Unprotect(token.Protect(request, Mercury), request);

        Assert.IsNotNull(scope);
        CollectionAssert.AreEqual(new[] { "planet", "element" }, scope.Interpretations.Select(i => i.Id).ToArray());
        Assert.AreEqual("element", scope.Best!.Id);
    }

    [TestMethod]
    public void ATamperedToken_ReadsAsAbsent()
    {
        var token = Token();
        var request = new ResearchRequest("Mercury");
        var sealedState = token.Protect(request, Mercury);
        var tampered = sealedState[..^4] + (sealedState[^4] == 'A' ? 'B' : 'A') + sealedState[^3..];

        Assert.IsNull(token.Unprotect(tampered, request));
        Assert.IsNull(token.Unprotect("plain text the client made up", request));
        Assert.IsNull(token.Unprotect("", request));
        Assert.IsNull(token.Unprotect(null, request));
    }

    [TestMethod]
    public void AnotherKeyRing_CantReadIt()
    {
        var request = new ResearchRequest("Mercury");
        var sealedState = Token().Protect(request, Mercury);

        Assert.IsNull(Token().Unprotect(sealedState, request), "a restarted server has a new in-memory key ring");
    }

    [TestMethod]
    public async Task AnExpiredToken_ReadsAsAbsent()
    {
        var token = Token(TimeSpan.FromMilliseconds(1));
        var request = new ResearchRequest("Mercury");
        var sealedState = token.Protect(request, Mercury);
        await Task.Delay(50);

        Assert.IsNull(token.Unprotect(sealedState, request));
    }

    [TestMethod]
    [DataRow("Venus", null, null)]
    [DataRow("Mercury", "ctx", null)]
    [DataRow("Mercury", null, "since 2020")]
    public void ATokenForOtherArguments_ReadsAsAbsent(string question, string? context, string? constraints)
    {
        var token = Token();
        var sealedState = token.Protect(new ResearchRequest("Mercury"), Mercury);

        Assert.IsNull(token.Unprotect(sealedState, new ResearchRequest(question, context, constraints)));
    }

    [TestMethod]
    public void TheArgumentHash_KeepsFieldsApart() =>
        Assert.AreNotEqual(
            ResearchScopeToken.ArgumentHash(new ResearchRequest("ab", "c")),
            ResearchScopeToken.ArgumentHash(new ResearchRequest("a", "bc")));
}

[TestClass]
public class ResearchToolsUnitTests
{
    private static readonly ResearchScope Mercury = new(
        [new("planet", "The planet"), new("element", "The element")], "element");

    private static ElicitResult Answer(string action, string? choice) => new()
    {
        Action = action,
        Content = choice is null ? null : new Dictionary<string, JsonElement>
        {
            [ResearchTools.AnswerKey] = JsonSerializer.SerializeToElement(choice),
        },
    };

    [TestMethod]
    public void Resolve_AcceptedValidChoice_IsChosen()
    {
        var resolved = ResearchTools.Resolve(Mercury, Answer("accept", "planet"));
        Assert.AreEqual("planet", resolved.Interpretation.Id);
        Assert.IsTrue(resolved.Chosen);
    }

    [TestMethod]
    public void Resolve_AnythingElse_IsTheBestReading_NotChosen()
    {
        foreach (var answer in new ElicitResult?[] { null, Answer("decline", null), Answer("cancel", null), Answer("accept", "band"), Answer("accept", null) })
        {
            var resolved = ResearchTools.Resolve(Mercury, answer);
            Assert.AreEqual("element", resolved.Interpretation.Id);
            Assert.IsFalse(resolved.Chosen);
        }
    }

    [TestMethod]
    public void BuildResult_WithoutSources_HasOneBlock()
    {
        var result = ResearchTools.BuildResult(new ResearchOutcome("the answer", []), resolved: null);

        Assert.AreEqual(1, result.Content.Count);
        Assert.AreEqual("the answer", ((TextContentBlock)result.Content[0]).Text);
    }
}

[TestClass]
public class ResearchHelpersTests
{
    [TestMethod]
    public void BuildUserMessage_IsTheBareQuestion_WhenNothingElseIsGiven() =>
        Assert.AreEqual("What is MCP?", ResearchRunner.BuildUserMessage(new ResearchRequest("What is MCP?")));

    [TestMethod]
    public void BuildUserMessage_CarriesContextConstraintsAndInterpretation()
    {
        var text = ResearchRunner.BuildUserMessage(new ResearchRequest("Mercury", "for a quiz", "only 2024", "The planet"));

        StringAssert.Contains(text, "Question: Mercury");
        StringAssert.Contains(text, "Research this interpretation of the question: The planet");
        StringAssert.Contains(text, "Context from the person asking: for a quiz");
        StringAssert.Contains(text, "Constraints: only 2024");
    }

    [TestMethod]
    public void SourceLog_KeepsFirstReadOrder_AndDropsRepeats()
    {
        var log = new SourceLog();
        log.Add("https://b.example", "B");
        log.Add("https://a.example", null);
        log.Add("https://B.example", "again");
        log.Add(" ", "blank");

        CollectionAssert.AreEqual(new[] { "https://b.example", "https://a.example" }, log.Sources.Select(s => s.Url).ToArray());
        Assert.AreEqual("B", log.Sources[0].Title);
        Assert.IsNull(log.Sources[1].Title);
    }

    [TestMethod]
    public void SourceLog_TitleFrom_ReadsTheFirstHeading()
    {
        Assert.AreEqual("Mercury (planet)", SourceLog.TitleFrom("Source: https://x\n\n# Mercury (planet)\n\nbody"));
        Assert.IsNull(SourceLog.TitleFrom("Source: https://x\n\nno heading"));
        Assert.IsNull(SourceLog.TitleFrom(null));
    }

    [TestMethod]
    [DataRow("2026-07-28", true)]
    [DataRow("2026-12-01", true)]
    [DataRow("2025-11-25", false)]
    [DataRow("2025-06-18", false)]
    [DataRow(null, false)]
    public void TheProtocolCheck_MatchesTheTasksExtension(string? version, bool expected) =>
        Assert.AreEqual(expected, ResearchCallMode.IsJuly2026OrLater(version));
}
