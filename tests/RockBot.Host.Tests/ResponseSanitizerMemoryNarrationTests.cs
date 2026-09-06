using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers <see cref="ResponseSanitizer.StripTrailingMemoryNarration"/>, added under
/// issue #397. The production failure is a reply that answers the user correctly and
/// then appends a sentence narrating the memory write. The strip must remove that
/// closing without touching the answer, and must leave genuine outcome reports alone.
/// </summary>
[TestClass]
public class ResponseSanitizerMemoryNarrationTests
{
    // ── Positive cases: captured from the live cluster on 2026-09-05 ─────────

    [TestMethod]
    public void StripsTrailingNarration_CathedralCityReproducer()
    {
        const string response =
            "That's the right window. Cathedral City sounds less like a nice idea and more " +
            "like a place that actually earns its keep for you, especially if the holidays " +
            "are over and the rental market cools off.\n\n" +
            "I've marked it as a winter trip goal tied to your joints and the dry air.";

        var result = ResponseSanitizer.StripTrailingMemoryNarration(response);

        StringAssert.StartsWith(result, "That's the right window.");
        Assert.IsFalse(result.Contains("I've marked it", StringComparison.Ordinal),
            "The memory-write narration closing must be removed.");
    }

    [TestMethod]
    public void StripsTrailingNarration_TravelPictureVariant()
    {
        const string response =
            "That tracks. Dry air can be the difference between tolerable and miserable with joints.\n\n" +
            "I've got Cathedral City in the travel picture now, as a place that's physically " +
            "easier on you, not just another dot on the map.";

        var result = ResponseSanitizer.StripTrailingMemoryNarration(response);

        Assert.AreEqual(
            "That tracks. Dry air can be the difference between tolerable and miserable with joints.",
            result);
    }

    [TestMethod]
    public void StripsTrailingNarration_SingleNewlineSeparator()
    {
        const string response =
            "The post-holiday dip is real — mid-January onward is usually the cheapest stretch, " +
            "and the weather is still good.\n" +
            "I've noted that in memory.";

        var result = ResponseSanitizer.StripTrailingMemoryNarration(response);

        Assert.IsFalse(result.Contains("noted that in memory", StringComparison.OrdinalIgnoreCase));
        StringAssert.StartsWith(result, "The post-holiday dip is real");
    }

    [TestMethod]
    public void StripsTrailingNarration_SubjectlessParticipleForm()
    {
        // Captured live on 2026-09-06: the model drops the subject entirely.
        const string response =
            "February works — that keeps the whole trip inside the good stretch of weather.\n\n" +
            "Added to the ledger: your sister might drive out for a few days too.";

        var result = ResponseSanitizer.StripTrailingMemoryNarration(response);

        Assert.AreEqual(
            "February works — that keeps the whole trip inside the good stretch of weather.",
            result);
    }

    // ── Negative cases: real outcomes the user needs to hear ────────────────

    [TestMethod]
    [DataRow("Done. The migration ran clean.\n\nI've saved the file to /tmp/report.csv.")]
    [DataRow("Both conflicts are resolved.\n\nI've added it to your todo list for Thursday.")]
    [DataRow("Thursday at 2pm works for everyone.\n\nI've put it on your calendar.")]
    [DataRow("The build is green.\n\nI've noted the flaky test in the issue.")]
    [DataRow("Here's the summary you asked for.\n\nLet me know if you want more detail.")]
    [DataRow("The upload finished.\n\nAdded the notes to the shared drive.")]
    [DataRow("Access is working again.\n\nLogged in to the portal without trouble.")]
    public void DoesNotStripGenuineOutcomeReports(string response)
    {
        Assert.AreEqual(response, ResponseSanitizer.StripTrailingMemoryNarration(response),
            $"Must not strip a real reported outcome: \"{response}\"");
    }

    [TestMethod]
    public void DoesNotStripWholeResponseNarration()
    {
        // A reply that is ONLY narration would be emptied by stripping. The substance
        // guard declines, leaving it to the AgentLoopRunner re-prompt guard.
        const string response = "I've marked it as a winter trip goal tied to your joints and the dry air.";

        Assert.AreEqual(response, ResponseSanitizer.StripTrailingMemoryNarration(response));
    }

    [TestMethod]
    public void LeavesOrdinaryReplyUnchanged()
    {
        const string response =
            "The dry air is doing real work there — that's a good reason to aim for January " +
            "rather than pushing it to spring.";

        Assert.AreEqual(response, ResponseSanitizer.StripTrailingMemoryNarration(response));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    public void HandlesEmptyInput(string? response)
    {
        Assert.AreEqual(response, ResponseSanitizer.StripTrailingMemoryNarration(response!));
    }

    [TestMethod]
    public void DoesNotDisturbTrailingOfferStripping()
    {
        // The two sanitizers are independent; running both must not corrupt the answer.
        const string response =
            "Mid-January is the sweet spot for both price and weather.\n\n" +
            "I've got that on the travel list now.\n\n" +
            "Would you like me to check flight prices?";

        var result = ResponseSanitizer.StripTrailingMemoryNarration(
            ResponseSanitizer.StripTrailingOffers(response));

        Assert.AreEqual("Mid-January is the sweet spot for both price and weather.", result);
    }
}
