using System.Text.Json;
using System.Reflection;

namespace RockBot.Observation.Tests;

[TestClass]
public class ObservationStateRoundTripTests
{
    private static readonly JsonSerializerOptions Options = ResolveJsonOptions();

    private static JsonSerializerOptions ResolveJsonOptions()
    {
        // Internal type; reach in via reflection to test that it's correctly configured.
        var assembly = typeof(ObservationState).Assembly;
        var optionsType = assembly.GetType("RockBot.Observation.ObservationStateJsonOptions")
            ?? throw new InvalidOperationException("ObservationStateJsonOptions not found");
        var instanceField = optionsType.GetField("Instance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Instance field not found");
        return (JsonSerializerOptions)instanceField.GetValue(null)!;
    }

    [TestMethod]
    public void EmptyState_RoundTripsCleanly()
    {
        var state = new ObservationState();
        var json = JsonSerializer.Serialize(state, Options);
        var roundTripped = JsonSerializer.Deserialize<ObservationState>(json, Options);

        Assert.IsNotNull(roundTripped);
        Assert.AreEqual(ObservationState.CurrentSchemaVersion, roundTripped.SchemaVersion);
        Assert.IsNull(roundTripped.LastDreamAt);
        Assert.AreEqual(0, roundTripped.Candidates.Count);
        Assert.AreEqual(0, roundTripped.Theories.Count);
        Assert.AreEqual(0, roundTripped.Snapshots.Count);
    }

    [TestMethod]
    public void PopulatedState_RoundTripsAllFields()
    {
        var observed = DateTimeOffset.Parse("2026-05-07T12:00:00Z");
        var state = new ObservationState
        {
            LastDreamAt = observed,
            Candidates =
            {
                new Candidate
                {
                    Id = "cand_001",
                    Text = "User reverts diffs that touch tests they did not ask about.",
                    ClusterId = "clust_42",
                    Count = 2,
                    FirstSeen = observed.AddDays(-15),
                    LastSeen = observed.AddDays(-2),
                    References =
                    {
                        new ObservationReference("conv_001", "turn_017", "...just revert that test change...", observed.AddDays(-15)),
                        new ObservationReference("conv_044", "turn_004", "...don't touch the test file...", observed.AddDays(-2)),
                    },
                },
            },
            Theories =
            {
                new Theory
                {
                    Id = "thry_001",
                    Text = "User prefers terse responses with no trailing summaries.",
                    PromotedAt = observed.AddDays(-50),
                    LastReinforced = observed.AddDays(-1),
                    SourceCandidateIds = { "cand_seed" },
                    References =
                    {
                        new ObservationReference("conv_010", "turn_003", "...just give me the diff...", observed.AddDays(-50)),
                    },
                },
            },
            Snapshots =
            {
                new Snapshot(observed.AddDays(-1), "# Theory of self\n\n(snapshot body)\n"),
            },
        };

        var json = JsonSerializer.Serialize(state, Options);
        var rt = JsonSerializer.Deserialize<ObservationState>(json, Options);

        Assert.IsNotNull(rt);
        Assert.AreEqual(observed, rt.LastDreamAt);

        Assert.AreEqual(1, rt.Candidates.Count);
        var cand = rt.Candidates[0];
        Assert.AreEqual("cand_001", cand.Id);
        Assert.AreEqual("clust_42", cand.ClusterId);
        Assert.AreEqual(2, cand.Count);
        Assert.AreEqual(2, cand.References.Count);
        Assert.AreEqual("conv_001", cand.References[0].ConversationId);
        Assert.AreEqual("turn_017", cand.References[0].TurnId);
        Assert.IsTrue(cand.References[0].Quote.Contains("revert that test"));

        Assert.AreEqual(1, rt.Theories.Count);
        var thry = rt.Theories[0];
        Assert.AreEqual("thry_001", thry.Id);
        Assert.AreEqual(1, thry.SourceCandidateIds.Count);
        Assert.AreEqual("cand_seed", thry.SourceCandidateIds[0]);

        Assert.AreEqual(1, rt.Snapshots.Count);
        Assert.IsTrue(rt.Snapshots[0].Markdown.Contains("snapshot body"));
    }

    [TestMethod]
    public void Json_UsesCamelCase()
    {
        var state = new ObservationState { LastDreamAt = DateTimeOffset.UnixEpoch };
        var json = JsonSerializer.Serialize(state, Options);

        Assert.IsTrue(json.Contains("\"schemaVersion\""), "Expected camelCase 'schemaVersion'");
        Assert.IsTrue(json.Contains("\"lastDreamAt\""), "Expected camelCase 'lastDreamAt'");
        Assert.IsFalse(json.Contains("\"SchemaVersion\""), "PascalCase should not appear");
    }

    [TestMethod]
    public void SchemaVersion_DefaultsToCurrent()
    {
        var state = new ObservationState();
        Assert.AreEqual(1, state.SchemaVersion);
        Assert.AreEqual(1, ObservationState.CurrentSchemaVersion);
    }
}
