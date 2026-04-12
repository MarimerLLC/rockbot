using RockBot.Agent.A2A;

namespace RockBot.Agent.A2A.Tests;

[TestClass]
public class InboundSkillMatcherTests
{
    // ── Exact ID match ──────────────────────────────────────────────────

    [TestMethod]
    public void Match_ExactId_NotifyUser() =>
        Assert.AreEqual("notify-user", InboundSkillMatcher.Match("notify-user"));

    [TestMethod]
    public void Match_ExactId_QueryAvailability() =>
        Assert.AreEqual("query-availability", InboundSkillMatcher.Match("query-availability"));

    [TestMethod]
    public void Match_ExactId_NegotiateMeeting() =>
        Assert.AreEqual("negotiate-meeting", InboundSkillMatcher.Match("negotiate-meeting"));

    [TestMethod]
    public void Match_ExactId_CaseInsensitive() =>
        Assert.AreEqual("notify-user", InboundSkillMatcher.Match("Notify-User"));

    // ── Known alias match ───────────────────────────────────────────────

    [TestMethod]
    public void Match_Alias_ScheduleMeeting() =>
        Assert.AreEqual("negotiate-meeting", InboundSkillMatcher.Match("schedule-meeting"));

    [TestMethod]
    public void Match_Alias_BookMeeting() =>
        Assert.AreEqual("negotiate-meeting", InboundSkillMatcher.Match("book-meeting"));

    [TestMethod]
    public void Match_Alias_CheckAvailability() =>
        Assert.AreEqual("query-availability", InboundSkillMatcher.Match("check-availability"));

    [TestMethod]
    public void Match_Alias_SendNotification() =>
        Assert.AreEqual("notify-user", InboundSkillMatcher.Match("send-notification"));

    [TestMethod]
    public void Match_Alias_CaseInsensitive() =>
        Assert.AreEqual("negotiate-meeting", InboundSkillMatcher.Match("Schedule-Meeting"));

    // ── BM25 fuzzy match ────────────────────────────────────────────────

    [TestMethod]
    public void Match_Fuzzy_MeetingSchedule() =>
        Assert.AreEqual("negotiate-meeting", InboundSkillMatcher.Match("meeting-schedule"));

    [TestMethod]
    public void Match_Fuzzy_UserNotification() =>
        Assert.AreEqual("notify-user", InboundSkillMatcher.Match("user-notification"));

    [TestMethod]
    public void Match_Fuzzy_AvailabilityStatus() =>
        Assert.AreEqual("query-availability", InboundSkillMatcher.Match("availability-status"));

    // ── No match ────────────────────────────────────────────────────────

    [TestMethod]
    public void Match_NoMatch_UnrelatedSkill() =>
        Assert.IsNull(InboundSkillMatcher.Match("deploy-kubernetes"));

    [TestMethod]
    public void Match_NoMatch_Empty() =>
        Assert.IsNull(InboundSkillMatcher.Match(""));

    [TestMethod]
    public void Match_NoMatch_Null() =>
        Assert.IsNull(InboundSkillMatcher.Match(null!));
}
