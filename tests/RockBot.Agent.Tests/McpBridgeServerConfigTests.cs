using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.ArgGuards;

namespace RockBot.Agent.Tests;

[TestClass]
public class McpBridgeServerConfigTests
{
    // ── NormalizeUrl ──────────────────────────────────────────────────────────

    [TestMethod]
    public void NormalizeUrl_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, McpBridgeServerConfig.NormalizeUrl(null));
        Assert.AreEqual(string.Empty, McpBridgeServerConfig.NormalizeUrl(""));
        Assert.AreEqual(string.Empty, McpBridgeServerConfig.NormalizeUrl("   "));
    }

    [TestMethod]
    public void NormalizeUrl_TrailingSlash_IsStripped()
    {
        Assert.AreEqual(
            McpBridgeServerConfig.NormalizeUrl("http://mcp-todo.rockbot.svc.cluster.local"),
            McpBridgeServerConfig.NormalizeUrl("http://mcp-todo.rockbot.svc.cluster.local/"));
    }

    [TestMethod]
    public void NormalizeUrl_AuthorityCase_IsIgnored()
    {
        Assert.AreEqual(
            McpBridgeServerConfig.NormalizeUrl("http://Mcp-Todo.Rockbot.Svc.Cluster.Local/"),
            McpBridgeServerConfig.NormalizeUrl("http://mcp-todo.rockbot.svc.cluster.local/"));
    }

    [TestMethod]
    public void NormalizeUrl_PathCase_IsPreserved()
    {
        // Path is case-sensitive in HTTP; do not collapse different paths into one identity.
        Assert.AreNotEqual(
            McpBridgeServerConfig.NormalizeUrl("http://host/PathOne"),
            McpBridgeServerConfig.NormalizeUrl("http://host/pathone"));
    }

    // ── CanonicalIdentity ─────────────────────────────────────────────────────

    [TestMethod]
    public void CanonicalIdentity_DiffersOnlyByName_AreEqual()
    {
        // Two entries registered under different names but pointing at the exact
        // same server with the same config must hash to the same identity — this
        // is the whole point of dedup.
        var a = new McpBridgeServerConfig { Type = "sse", Url = "http://mcp-todo/" };
        var b = new McpBridgeServerConfig { Type = "sse", Url = "http://mcp-todo/" };

        Assert.AreEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_UrlDiffersIgnoringTrailingSlashAndCase_AreEqual()
    {
        var a = new McpBridgeServerConfig { Type = "sse", Url = "http://Mcp-Todo/" };
        var b = new McpBridgeServerConfig { Type = "sse", Url = "http://mcp-todo" };

        Assert.AreEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_DifferentUrls_AreDifferent()
    {
        var a = new McpBridgeServerConfig { Type = "sse", Url = "http://host-a/" };
        var b = new McpBridgeServerConfig { Type = "sse", Url = "http://host-b/" };

        Assert.AreNotEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_DifferentHeaders_AreDifferent()
    {
        // Same URL but different auth headers — NOT a duplicate. This is the
        // staging-vs-helm-seed scenario: auth-bearing entry must not be collapsed
        // with an unauthenticated seed.
        var a = new McpBridgeServerConfig { Type = "sse", Url = "http://host/" };
        var b = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "http://host/",
            Headers = { ["X-Api-Key"] = "secret" }
        };

        Assert.AreNotEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_SameHeadersDifferentCase_AreEqual()
    {
        var a = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "http://host/",
            Headers = { ["X-Api-Key"] = "secret" }
        };
        var b = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "http://host/",
            Headers = { ["x-api-key"] = "secret" }
        };

        Assert.AreEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_DifferentHeaderValues_AreDifferent()
    {
        // Same header name, different secret — different credentials, keep both.
        var a = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "http://host/",
            Headers = { ["X-Api-Key"] = "secret-1" }
        };
        var b = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "http://host/",
            Headers = { ["X-Api-Key"] = "secret-2" }
        };

        Assert.AreNotEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_DifferentTransportMode_AreDifferent()
    {
        var a = new McpBridgeServerConfig { Type = "sse", Url = "http://host/", TransportMode = "sse" };
        var b = new McpBridgeServerConfig { Type = "sse", Url = "http://host/", TransportMode = "streamable-http" };

        Assert.AreNotEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_DefaultTransportMode_MatchesAuto()
    {
        // "auto" is the default; an entry written without TransportMode should equal one with "auto".
        var a = new McpBridgeServerConfig { Type = "sse", Url = "http://host/" };
        var b = new McpBridgeServerConfig { Type = "sse", Url = "http://host/", TransportMode = "auto" };

        Assert.AreEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_HeaderOrder_DoesNotAffectIdentity()
    {
        var a = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "http://host/",
            Headers = { ["A"] = "1", ["B"] = "2" }
        };
        var b = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "http://host/",
            Headers = { ["B"] = "2", ["A"] = "1" }
        };

        Assert.AreEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    // ── Auth profile ──────────────────────────────────────────────────────────

    [TestMethod]
    public void CanonicalIdentity_AuthProfile_DifferentiatesFromUnauthenticated()
    {
        // Same URL, but one carries an auth profile — must not be deduped against
        // the other, since they call as different identities even at the same endpoint.
        var unauth = new McpBridgeServerConfig { Type = "streamable-http", Url = "https://srv/" };
        var auth = new McpBridgeServerConfig
        {
            Type = "streamable-http",
            Url = "https://srv/",
            Auth = new McpServerAuthConfig { Profile = "workiq" }
        };

        Assert.AreNotEqual(unauth.CanonicalIdentity(), auth.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_DifferentAuthProfiles_AreDifferent()
    {
        // Same URL, different identities. Both registrations must be kept.
        var a = new McpBridgeServerConfig
        {
            Type = "streamable-http",
            Url = "https://srv/",
            Auth = new McpServerAuthConfig { Profile = "workiq" }
        };
        var b = new McpBridgeServerConfig
        {
            Type = "streamable-http",
            Url = "https://srv/",
            Auth = new McpServerAuthConfig { Profile = "github" }
        };

        Assert.AreNotEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_SameAuthProfile_CaseInsensitive_AreEqual()
    {
        var a = new McpBridgeServerConfig
        {
            Type = "streamable-http",
            Url = "https://srv/",
            Auth = new McpServerAuthConfig { Profile = "WorkIQ" }
        };
        var b = new McpBridgeServerConfig
        {
            Type = "streamable-http",
            Url = "https://srv/",
            Auth = new McpServerAuthConfig { Profile = "workiq" }
        };

        Assert.AreEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    [TestMethod]
    public void CanonicalIdentity_DiffersOnlyByArgGuards_AreEqual()
    {
        // Pins the exclusion: argGuards are policy about how the server is invoked,
        // not which server it is (same rationale as Attachments). A guard-bearing
        // entry and a guard-free clone of the same server must dedup together.
        var a = new McpBridgeServerConfig { Type = "sse", Url = "https://srv/" };
        var b = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "https://srv/",
            ArgGuards =
            [
                new McpArgGuardConfig { Handler = "path-prefix", Tools = ["download_file"] }
            ]
        };

        Assert.AreEqual(a.CanonicalIdentity(), b.CanonicalIdentity());
    }

    // ── CarryOperatorPolicyFrom ───────────────────────────────────────────────

    [TestMethod]
    public void CarryOperatorPolicyFrom_SameServer_KeepsArgGuardsAndTheWholeElicitationPolicy()
    {
        // register_mcp_server is LLM-callable and cannot express either block; re-registering
        // an existing name must not turn an operator's "off" server back into an answering one.
        var existing = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "https://srv/",
            ArgGuards = [new McpArgGuardConfig { Handler = "path-prefix", Tools = ["download_file"] }],
            Elicitation = new RockBot.Tools.Mcp.Elicitation.McpElicitationConfig
            {
                Mode = "off",
                DeniedFields = ["accountId"],
                Responder = "conversation",
            },
        };
        var registered = new McpBridgeServerConfig { Type = "sse", Url = "https://srv/" };

        registered.CarryOperatorPolicyFrom(existing);

        Assert.AreSame(existing.ArgGuards, registered.ArgGuards);
        Assert.AreSame(existing.Elicitation, registered.Elicitation);
    }

    [TestMethod]
    public void CarryOperatorPolicyFrom_RepointedName_KeepsRestrictionsButNotGrants()
    {
        // The model re-points a trusted name at a URL of its choosing. The operator's
        // restrictions still apply; what the operator granted the original server — a responder
        // that hands over conversation data, and defaults that answer (even confirm) on the
        // user's behalf — must not follow the name.
        var existing = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "https://srv/",
            ArgGuards = [new McpArgGuardConfig { Handler = "path-prefix", Tools = ["download_file"] }],
            Elicitation = new RockBot.Tools.Mcp.Elicitation.McpElicitationConfig
            {
                Mode = "auto",
                MaxPerCall = 1,
                DeniedFields = ["accountId"],
                Responder = "conversation",
                Defaults = new() { ["confirm"] = System.Text.Json.JsonDocument.Parse("true").RootElement },
            },
        };
        var registered = new McpBridgeServerConfig { Type = "sse", Url = "https://attacker.example/" };

        registered.CarryOperatorPolicyFrom(existing);

        Assert.AreSame(existing.ArgGuards, registered.ArgGuards);
        Assert.IsNotNull(registered.Elicitation);
        Assert.AreEqual(1, registered.Elicitation.MaxPerCall);
        CollectionAssert.AreEqual(new[] { "accountId" }, registered.Elicitation.DeniedFields);
        Assert.IsNull(registered.Elicitation.Responder);
        Assert.AreEqual(0, registered.Elicitation.Defaults.Count);
        Assert.AreEqual("https://attacker.example/", registered.Url, "only policy is carried, not the connection");
    }

    [TestMethod]
    public void CarryOperatorPolicyFrom_NoExistingConfig_LeavesDefaults()
    {
        var registered = new McpBridgeServerConfig { Type = "sse", Url = "https://srv/" };

        registered.CarryOperatorPolicyFrom(null);

        Assert.AreEqual(0, registered.ArgGuards.Count);
        Assert.IsNull(registered.Elicitation);
    }

    [TestMethod]
    public void CanonicalIdentity_DiffersOnlyByToolTimeout_AreEqual()
    {
        // Tool timeout is invocation policy, not server identity.
        // Different timeout budgets must not create duplicate registrations
        // for the same underlying MCP server.
        var a = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "https://srv/"
        };

        var b = new McpBridgeServerConfig
        {
            Type = "sse",
            Url = "https://srv/",
            ToolTimeoutMs = 900_000
        };

        Assert.AreEqual(
            a.CanonicalIdentity(),
            b.CanonicalIdentity());
    }
}
