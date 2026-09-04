namespace RockBot.Host.Tests;

/// <summary>
/// The audit's findings have to explain themselves. <c>chain-depth-threshold</c> is a stable
/// identifier and useless to a person, so every invariant the audit can emit must have a
/// plain-language definition behind it.
/// </summary>
[TestClass]
public class MemoryAuditGlossaryTests
{
    /// <summary>Every invariant name the checker can produce.</summary>
    private static readonly string[] AllInvariants =
    [
        MemoryAuditInvariants.MergedFromResolves,
        MemoryAuditInvariants.ArchiveFieldsPresent,
        MemoryAuditInvariants.LiveNotMergeSource,
        MemoryAuditInvariants.MergeChainUnbroken,
        MemoryAuditInvariants.NoHardDeleteOutsidePurge,
        MemoryAuditInvariants.NoRepeatedRejection,
        MemoryAuditInvariants.NetGrowthThreshold,
        MemoryAuditInvariants.ChainDepthThreshold,
        MemoryAuditInvariants.RejectedMergesThreshold,
        MemoryAuditInvariants.LossPercentThreshold,
        MemoryAuditInvariants.NoMalformedFiles
    ];

    [TestMethod]
    public void EveryInvariantHasAPlainLanguageDefinition()
    {
        // The guard that matters: adding an invariant without a definition ships a warning the
        // agent can report but cannot explain.
        var missing = AllInvariants.Where(n => MemoryAuditGlossary.Describe(n) is null).ToList();

        Assert.AreEqual(0, missing.Count,
            $"No glossary entry for: {string.Join(", ", missing)}");
    }

    [TestMethod]
    public void TheGlossaryDefinesNothingTheCheckerCannotEmit()
    {
        var orphans = MemoryAuditGlossary.All.Keys.Except(AllInvariants, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.AreEqual(0, orphans.Count,
            $"Glossary defines invariants that no longer exist: {string.Join(", ", orphans)}");
    }

    [TestMethod]
    public void DefinitionsCarryTheSeverityTheCheckerActuallyAssigns()
    {
        Assert.AreEqual(MemoryAuditStatuses.Alert,
            MemoryAuditGlossary.Describe(MemoryAuditInvariants.NoHardDeleteOutsidePurge)!.Severity);
        Assert.AreEqual(MemoryAuditStatuses.Alert,
            MemoryAuditGlossary.Describe(MemoryAuditInvariants.MergeChainUnbroken)!.Severity);
        Assert.AreEqual(MemoryAuditStatuses.Alert,
            MemoryAuditGlossary.Describe(MemoryAuditInvariants.LossPercentThreshold)!.Severity);
        Assert.AreEqual(MemoryAuditStatuses.Warning,
            MemoryAuditGlossary.Describe(MemoryAuditInvariants.ChainDepthThreshold)!.Severity);
    }

    [TestMethod]
    public void DefinitionsAvoidTheJargonTheyExistToTranslate()
    {
        // "Merge chain depth" and "invariant" are exactly the words a non-technical reader
        // would have to look up, which is the whole reason this file exists.
        string[] banned = ["invariant", "corpus", "provenance", "idempotent", "jaccard", "shingle"];

        foreach (var (name, definition) in MemoryAuditGlossary.All)
        {
            var prose = $"{definition.Title} {definition.WhatItMeans} {definition.WhatToDo}".ToLowerInvariant();
            foreach (var word in banned)
                Assert.IsFalse(prose.Contains(word),
                    $"'{name}' explains itself using the jargon word '{word}'.");
        }
    }

    [TestMethod]
    public void AnUnknownNameYieldsNullRatherThanFiller()
    {
        // A generic "a memory-health check failed" would hide a missing definition instead of
        // surfacing it.
        Assert.IsNull(MemoryAuditGlossary.Describe("not-a-real-invariant"));
    }

    [TestMethod]
    public void TheGuideDocumentCoversEveryFindingAndNamesItsTools()
    {
        var document = new MemoryAuditSkillProvider().GetDocument();

        foreach (var name in AllInvariants)
            StringAssert.Contains(document, name, $"The guide omits {name}.");

        StringAssert.Contains(document, "get_memory_audit");
        StringAssert.Contains(document, "get_memory_audit_trend");
        StringAssert.Contains(document, "recall");
    }

    [TestMethod]
    public void TheGuideIsNamedSoTheDirectivesPointerResolves()
    {
        var provider = new MemoryAuditSkillProvider();

        // directives.md tells the agent to call get_tool_guide("memory-audit").
        Assert.AreEqual("memory-audit", provider.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(provider.Summary));
    }
}
