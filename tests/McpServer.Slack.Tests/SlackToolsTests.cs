using System.Net;
using System.Text.Json;

namespace McpServer.Slack.Tests;

[TestClass]
public sealed class SlackToolsTests : SlackServerTestBase
{
    private const string Channels = """
        {"ok":true,"channels":[
          {"id":"C0GENERAL1","name":"general","is_private":false,"topic":{"value":"Company news"}},
          {"id":"G0SECRET01","name":"secret","is_private":true,"topic":{"value":""}},
          {"id":"D0DIRECT01","is_im":true,"user":"U0ALICE01"}],
         "response_metadata":{"next_cursor":"next-1"}}
        """;

    private static string History(params object[] messages) =>
        JsonSerializer.Serialize(new { ok = true, messages, response_metadata = new { next_cursor = "" } });

    [TestMethod]
    public async Task ListTools_AreAllReadOnly()
    {
        var tools = await (await ClientAsync()).ListToolsAsync();

        CollectionAssert.AreEquivalent(
            new[] { "list_workspaces", "list_conversations", "get_history", "get_thread", "search_messages", "list_unread", "get_user" },
            tools.Select(t => t.Name).ToArray());
        Assert.IsTrue(tools.All(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true));
    }

    [TestMethod]
    public async Task ListWorkspaces_ChecksEachTokenWithAuthTest()
    {
        var workspaces = await CallOkAsync("list_workspaces");

        Assert.AreEqual(2, workspaces.GetArrayLength());
        Assert.AreEqual("work", workspaces[0].GetProperty("name").GetString());
        Assert.AreEqual("T1", workspaces[0].GetProperty("teamId").GetString());
        CollectionAssert.AreEquivalent(new[] { "xoxp-work", "xoxp-home" }, Slack.Of("auth.test").Select(c => c.Token).ToArray());
    }

    [TestMethod]
    public async Task TokenForAnotherTeam_IsRefused()
    {
        Slack.Responses["auth.test"] = _ => FakeSlack.Json("""{"ok":true,"team":"Other","team_id":"T9","user":"simon"}""");

        StringAssert.Contains(await CallErrorAsync("list_conversations", new() { ["workspace"] = "work" }), "belongs to team T9, not T1");
        Assert.AreEqual(0, Slack.Of("users.conversations").Count());
    }

    [TestMethod]
    public async Task UnknownWorkspace_IsErrorNamingTheConfiguredOnes()
    {
        var error = await CallErrorAsync("get_history", new() { ["workspace"] = "acme", ["channel"] = "C0GENERAL1" });

        StringAssert.Contains(error, "unknown workspace 'acme'; configured workspaces: work, home");
        Assert.AreEqual(0, Slack.Calls.Count);
    }

    [TestMethod]
    public async Task ListConversations_MapsTypesAndNamesDmsAfterThePerson()
    {
        Slack.Responses["users.conversations"] = _ => FakeSlack.Json(Channels);

        var page = await CallOkAsync("list_conversations", new() { ["workspace"] = "home", ["types"] = "public, dm", ["top"] = 10 });

        var call = Slack.Of("users.conversations").Single();
        Assert.AreEqual("xoxp-home", call.Token);
        Assert.AreEqual("public_channel,im", call.Form["types"]);
        Assert.AreEqual("10", call.Form["limit"]);
        Assert.AreEqual("true", call.Form["exclude_archived"]);
        var items = page.GetProperty("items");
        Assert.AreEqual("#general", items[0].GetProperty("name").GetString());
        Assert.AreEqual("Company news", items[0].GetProperty("topic").GetString());
        Assert.AreEqual("private", items[1].GetProperty("type").GetString());
        Assert.AreEqual("@NameU0ALICE01", items[2].GetProperty("name").GetString());
        Assert.AreEqual("dm", items[2].GetProperty("type").GetString());
        Assert.AreEqual("next-1", page.GetProperty("nextCursor").GetString());
    }

    [TestMethod]
    public async Task ListConversations_UnknownType_IsError()
    {
        StringAssert.Contains(await CallErrorAsync("list_conversations", new() { ["workspace"] = "work", ["types"] = "public,bots" }), "unknown type 'bots'");
    }

    [TestMethod]
    public async Task GetHistory_ResolvesChannelName_ConvertsDates_AndRendersMentions()
    {
        Slack.Responses["users.conversations"] = _ => FakeSlack.Json(Channels);
        Slack.Responses["conversations.history"] = _ => FakeSlack.Json(History(
            new { ts = "1760830200.000100", user = "U0BOB0001", text = "Hi <@U0ALICE01>, see <#C0GENERAL1|general> and <https://x.example/a|the doc> &amp; <!here>", reply_count = 3 },
            new { ts = "1760830100.000200", bot_profile = new { name = "Deploy bot" }, text = "deployed", files = new[] { new { name = "log.txt" } } }));

        var page = await CallOkAsync("get_history", new()
        {
            ["workspace"] = "work", ["channel"] = "#General", ["since"] = "2026-10-19", ["until"] = "2026-10-19T12:00", ["top"] = 20
        });

        var call = Slack.Of("conversations.history").Single();
        Assert.AreEqual("C0GENERAL1", call.Form["channel"]);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-19T00:00:00+11:00").ToUnixTimeSeconds().ToString(), call.Form["oldest"]);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-19T12:00:00+11:00").ToUnixTimeSeconds().ToString(), call.Form["latest"]);
        var first = page.GetProperty("items")[0];
        Assert.AreEqual("NameU0BOB0001", first.GetProperty("user").GetString());
        Assert.AreEqual("Hi @NameU0ALICE01, see #general and the doc (https://x.example/a) & @here", first.GetProperty("text").GetString());
        Assert.AreEqual(3, first.GetProperty("replies").GetInt32());
        StringAssert.EndsWith(first.GetProperty("time").GetString(), "+11:00");
        var second = page.GetProperty("items")[1];
        Assert.AreEqual("Deploy bot", second.GetProperty("user").GetString());
        Assert.AreEqual("log.txt", second.GetProperty("files")[0].GetString());
        Assert.AreEqual(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [TestMethod]
    public async Task GetHistory_UsersAreLookedUpOncePerWorkspace()
    {
        Slack.Responses["conversations.history"] = _ => FakeSlack.Json(History(
            new { ts = "1760830200.000100", user = "U0BOB0001", text = "<@U0BOB0001> one" },
            new { ts = "1760830100.000100", user = "U0BOB0001", text = "two" }));

        await CallOkAsync("get_history", new() { ["workspace"] = "work", ["channel"] = "C0GENERAL1" });
        await CallOkAsync("get_history", new() { ["workspace"] = "work", ["channel"] = "C0GENERAL1" });

        Assert.AreEqual(1, Slack.Of("users.info").Count());
        Assert.AreEqual(1, Slack.Of("auth.test").Count());
    }

    [TestMethod]
    public async Task GetHistory_LongMessage_IsTruncated()
    {
        Slack.Responses["conversations.history"] = _ => FakeSlack.Json(History(new { ts = "1760830200.000100", text = new string('x', 900) }));

        var page = await CallOkAsync("get_history", new() { ["workspace"] = "work", ["channel"] = "C0GENERAL1" });

        Assert.AreEqual(500, page.GetProperty("items")[0].GetProperty("text").GetString()!.Length);
        Assert.IsTrue(page.GetProperty("items")[0].GetProperty("textTruncated").GetBoolean());
    }

    [TestMethod]
    public async Task GetHistory_UnknownChannelName_IsError()
    {
        Slack.Responses["users.conversations"] = _ => FakeSlack.Json("""{"ok":true,"channels":[]}""");

        StringAssert.Contains(await CallErrorAsync("get_history", new() { ["workspace"] = "work", ["channel"] = "#nope" }), "no channel named '#nope'");
    }

    [TestMethod]
    [DataRow("2026-10-19T12:00", "2026-10-19T09:00", "until must be after since")]
    [DataRow("yesterday", null, "since must be a date or time")]
    public async Task GetHistory_BadDates_AreErrorsWithoutCallingSlack(string since, string? until, string message)
    {
        var args = new Dictionary<string, object?> { ["workspace"] = "work", ["channel"] = "C0GENERAL1", ["since"] = since };
        if (until is not null)
            args["until"] = until;

        StringAssert.Contains(await CallErrorAsync("get_history", args), message);
        Assert.AreEqual(0, Slack.Calls.Count);
    }

    [TestMethod]
    public async Task GetThread_PassesTs()
    {
        Slack.Responses["conversations.replies"] = _ => FakeSlack.Json(History(
            new { ts = "1760830200.000100", thread_ts = "1760830200.000100", text = "parent" },
            new { ts = "1760830300.000100", thread_ts = "1760830200.000100", text = "reply" }));

        var page = await CallOkAsync("get_thread", new() { ["workspace"] = "work", ["channel"] = "C0GENERAL1", ["ts"] = "1760830200.000100" });

        Assert.AreEqual("1760830200.000100", Slack.Of("conversations.replies").Single().Form["ts"]);
        var items = page.GetProperty("items");
        Assert.AreEqual(JsonValueKind.Null, items[0].GetProperty("threadTs").ValueKind);
        Assert.AreEqual("1760830200.000100", items[1].GetProperty("threadTs").GetString());
    }

    [TestMethod]
    public async Task GetThread_BadTs_IsError()
    {
        StringAssert.Contains(await CallErrorAsync("get_thread", new() { ["workspace"] = "work", ["channel"] = "C0GENERAL1", ["ts"] = "abc" }), "ts must look like");
    }

    [TestMethod]
    public async Task SearchMessages_PassesQueryAndReturnsChannelAndPermalink()
    {
        Slack.Responses["search.messages"] = _ => FakeSlack.Json("""
            {"ok":true,"messages":{"matches":[
              {"ts":"1760830200.000100","user":"U0BOB0001","username":"bob","text":"budget is <@U0ALICE01|alice>'s",
               "channel":{"id":"C0FIN00001","name":"finance"},"permalink":"https://x.slack.com/archives/C0FIN00001/p1"}]}}
            """);

        var hits = await CallOkAsync("search_messages", new() { ["workspace"] = "work", ["query"] = "budget in:#finance", ["top"] = 5, ["sort"] = "score" });

        var call = Slack.Of("search.messages").Single();
        Assert.AreEqual("budget in:#finance", call.Form["query"]);
        Assert.AreEqual("5", call.Form["count"]);
        Assert.AreEqual("score", call.Form["sort"]);
        Assert.AreEqual("bob", hits[0].GetProperty("user").GetString());
        Assert.AreEqual("budget is @alice's", hits[0].GetProperty("text").GetString());
        Assert.AreEqual("#finance", hits[0].GetProperty("channel").GetString());
        Assert.AreEqual("https://x.slack.com/archives/C0FIN00001/p1", hits[0].GetProperty("permalink").GetString());
    }

    [TestMethod]
    public async Task GetUser_ReturnsProfile_AndRejectsNonIds()
    {
        Slack.Responses["users.info"] = _ => FakeSlack.Json("""
            {"ok":true,"user":{"id":"U0ALICE01","name":"alice","real_name":"Alice A","tz":"Australia/Sydney",
             "profile":{"display_name":"","real_name":"Alice Adams","title":"CTO","email":"alice@example.com"}}}
            """);

        var user = await CallOkAsync("get_user", new() { ["workspace"] = "work", ["user"] = "U0ALICE01" });

        Assert.AreEqual("alice", user.GetProperty("name").GetString());
        Assert.AreEqual("Alice Adams", user.GetProperty("realName").GetString());
        Assert.AreEqual("alice@example.com", user.GetProperty("email").GetString());
        StringAssert.Contains(await CallErrorAsync("get_user", new() { ["workspace"] = "work", ["user"] = "@alice" }), "user id");
    }

    [TestMethod]
    public async Task MissingScope_GivesAHint()
    {
        Slack.Responses["search.messages"] = _ => FakeSlack.Json("""{"ok":false,"error":"missing_scope","needed":"search:read"}""");

        var error = await CallErrorAsync("search_messages", new() { ["workspace"] = "work", ["query"] = "x" });

        StringAssert.Contains(error, "missing_scope");
        StringAssert.Contains(error, "search:read");
        Assert.IsFalse(error.Contains("xoxp-"));
    }

    [TestMethod]
    public async Task RateLimited_RetriesOnceAfterRetryAfter()
    {
        var calls = 0;
        Slack.Responses["conversations.history"] = _ =>
        {
            if (calls++ > 0)
                return FakeSlack.Json(History());
            var limited = FakeSlack.Json("{}", HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return limited;
        };

        await CallOkAsync("get_history", new() { ["workspace"] = "work", ["channel"] = "C0GENERAL1" });

        Assert.AreEqual(2, Slack.Of("conversations.history").Count());
    }

    [TestMethod]
    public async Task ListUnread_KeepsMessagesAfterLastRead_PerConversation_NewestFirst()
    {
        Slack.Responses["search.messages"] = _ => FakeSlack.Json("""
            {"ok":true,"messages":{"paging":{"pages":1},"matches":[
              {"ts":"1760900300.000100","user":"U0BOB0001","text":"new in general","channel":{"id":"C0GENERAL1","name":"general"},"permalink":"https://x/p1"},
              {"ts":"1760900250.000100","user":"U0BOB0001","text":"reply in thread","channel":{"id":"C0GENERAL1","name":"general"},"permalink":"https://x/p2?thread_ts=1760900000.000100"},
              {"ts":"1760900200.000100","user":"U0ALICE01","text":"dm one","channel":{"id":"D0DIRECT01","name":"U0ALICE01","is_im":true,"user":"U0ALICE01"},"permalink":"https://x/p3"},
              {"ts":"1760900100.000100","user":"U0BOB0001","text":"already read","channel":{"id":"C0GENERAL1","name":"general"},"permalink":"https://x/p4"},
              {"ts":"1760900050.000100","user":"U0BOB0001","text":"old","channel":{"id":"C0QUIET001","name":"quiet"},"permalink":"https://x/p5"}]}}
            """);
        Slack.Responses["conversations.info"] = c => FakeSlack.Json(c.Form["channel"] switch
        {
            "C0GENERAL1" => """{"ok":true,"channel":{"id":"C0GENERAL1","last_read":"1760900150.000000"}}""",
            "D0DIRECT01" => """{"ok":true,"channel":{"id":"D0DIRECT01","last_read":"0000000000.000000","unread_count_display":4}}""",
            _ => """{"ok":true,"channel":{"id":"C0QUIET001","last_read":"1760900060.000000"}}"""
        });

        var report = await CallOkAsync("list_unread", new() { ["workspace"] = "work", ["days"] = 3 });

        var search = Slack.Of("search.messages").Single();
        StringAssert.Contains(search.Form["query"], "-from:me");
        StringAssert.StartsWith(search.Form["query"], "after:");
        Assert.AreEqual("timestamp", search.Form["sort"]);
        var conversations = report.GetProperty("conversations");
        Assert.AreEqual(2, conversations.GetArrayLength());

        var general = conversations[0];
        Assert.AreEqual("#general", general.GetProperty("name").GetString());
        Assert.AreEqual(1, general.GetProperty("unreadCount").GetInt32());
        Assert.AreEqual("new in general", general.GetProperty("messages")[0].GetProperty("text").GetString());

        var dm = conversations[1];
        Assert.AreEqual("dm", dm.GetProperty("type").GetString());
        Assert.AreEqual("@NameU0ALICE01", dm.GetProperty("name").GetString());
        Assert.AreEqual(4, dm.GetProperty("unreadCount").GetInt32(), "Slack's own DM count wins when it's higher");
        Assert.IsFalse(report.GetProperty("scanTruncated").GetBoolean());
    }

    [TestMethod]
    public async Task ListUnread_SearchDate_IsDaysBeforeTodayInTheConfiguredZone()
    {
        Slack.Responses["search.messages"] = _ => FakeSlack.Json("""{"ok":true,"messages":{"matches":[]}}""");

        var report = await CallOkAsync("list_unread", new() { ["workspace"] = "work", ["days"] = 2 });

        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).Date;
        Assert.AreEqual(today.AddDays(-2).ToString("yyyy-MM-dd"), report.GetProperty("since").GetString());
        StringAssert.StartsWith(Slack.Of("search.messages").Single().Form["query"], $"after:{today.AddDays(-3):yyyy-MM-dd}");
        Assert.AreEqual(0, report.GetProperty("conversations").GetArrayLength());
        Assert.AreEqual(0, Slack.Of("conversations.info").Count());
    }

    [TestMethod]
    public async Task ListUnread_CountsOnly_FetchesNoMessageText()
    {
        Slack.Responses["search.messages"] = _ => FakeSlack.Json("""
            {"ok":true,"messages":{"matches":[{"ts":"1760900300.000100","user":"U0BOB0001","text":"hi","channel":{"id":"C0GENERAL1","name":"general"}}]}}
            """);
        Slack.Responses["conversations.info"] = _ => FakeSlack.Json("""{"ok":true,"channel":{"last_read":"0"}}""");

        var report = await CallOkAsync("list_unread", new() { ["workspace"] = "work", ["messages_per_conversation"] = 0 });

        Assert.AreEqual(0, report.GetProperty("conversations")[0].GetProperty("messages").GetArrayLength());
        Assert.AreEqual(0, Slack.Of("users.info").Count());
    }

    [TestMethod]
    [DataRow("days", 0, "days must be between 1 and 30")]
    [DataRow("top", 51, "top must be between 1 and 50")]
    [DataRow("messages_per_conversation", 11, "messages_per_conversation must be between 0 and 10")]
    public async Task ListUnread_BadArguments_AreErrors(string arg, int value, string message)
    {
        StringAssert.Contains(await CallErrorAsync("list_unread", new() { ["workspace"] = "work", [arg] = value }), message);
        Assert.AreEqual(0, Slack.Calls.Count);
    }

    [TestMethod]
    public async Task GetHistory_AcceptsIsoInstantsWithOffsetOrZ()
    {
        Slack.Responses["conversations.history"] = _ => FakeSlack.Json(History());

        await CallOkAsync("get_history", new() { ["workspace"] = "work", ["channel"] = "C0GENERAL1", ["since"] = "2026-10-19T00:00:00+11:00", ["until"] = "2026-10-19T03:00Z" });

        var call = Slack.Of("conversations.history").Single();
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-18T13:00:00Z").ToUnixTimeSeconds().ToString(), call.Form["oldest"]);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-19T03:00:00Z").ToUnixTimeSeconds().ToString(), call.Form["latest"]);
    }

    [TestMethod]
    public async Task ListUnread_TypesFilter_KeepsOnlyThoseConversations()
    {
        Slack.Responses["search.messages"] = _ => FakeSlack.Json("""
            {"ok":true,"messages":{"matches":[
              {"ts":"1760900300.000100","user":"U0BOB0001","text":"channel","channel":{"id":"C0GENERAL1","name":"general"}},
              {"ts":"1760900200.000100","user":"U0ALICE01","text":"dm","channel":{"id":"D0DIRECT01","name":"U0ALICE01","is_im":true,"user":"U0ALICE01"}}]}}
            """);
        Slack.Responses["conversations.info"] = _ => FakeSlack.Json("""{"ok":true,"channel":{"last_read":"0"}}""");

        var report = await CallOkAsync("list_unread", new() { ["workspace"] = "work", ["types"] = "dm, group" });

        Assert.AreEqual(1, report.GetProperty("conversations").GetArrayLength());
        Assert.AreEqual("dm", report.GetProperty("conversations")[0].GetProperty("type").GetString());
        Assert.AreEqual("D0DIRECT01", Slack.Of("conversations.info").Single().Form["channel"]);
        StringAssert.Contains(await CallErrorAsync("list_unread", new() { ["workspace"] = "work", ["types"] = "bots" }), "unknown type 'bots'");
    }
}
