using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

/// <summary>
/// The best-effort RFC 6570 matcher behind <c>mcp_read_resource</c>'s unknown-URI hint (#617),
/// with the case table of mcp-aggregator#46 it was ported from.
/// </summary>
[TestClass]
public class McpResourceUriTemplateTests
{
    [TestMethod]
    [DataRow("docs://files/{name}", "docs://files/readme.md", true)]
    [DataRow("docs://files/{name}", "docs://files/a/b", false)]
    [DataRow("docs://files/{name}.md", "docs://files/readme.md", true)]
    [DataRow("pair://{a,b}", "pair://x,y", true)]
    [DataRow("git://repo/{+path}", "git://repo/src/a/b.cs", true)]
    [DataRow("git://repo/{+path}", "git://repo/", false)]
    [DataRow("doc://page{#section}", "doc://page#intro", true)]
    [DataRow("doc://page{#section}", "doc://page", true)]
    [DataRow("fs://root{/path}", "fs://root/a/b", true)]
    [DataRow("fs://root{/path}", "fs://root", true)]
    [DataRow("search://q{?term,limit}", "search://q?term=x&limit=5", true)]
    [DataRow("search://q{?term,limit}", "search://q", true)]
    [DataRow("search://q{?term}{&limit}", "search://q?term=x&limit=5", true)]
    [DataRow("file://report{.ext}", "file://report.pdf", true)]
    [DataRow("file://report{.ext}", "file://report", true)]
    [DataRow("matrix://m{;v}", "matrix://m;v=1", true)]
    [DataRow("docs://readme.md", "docs://readme.md", true)]
    [DataRow("docs://readme.md", "docs://readme.md.bak", false)]
    [DataRow("docs://readme.md", "docs://readmeXmd", false)]
    [DataRow("docs://{unclosed", "docs://{unclosed", true)]
    public void Matches_ApproximatesRfc6570(string template, string uri, bool expected) =>
        Assert.AreEqual(expected, McpResourceUriTemplate.Matches(template, uri), $"{template} vs {uri}");

    [TestMethod]
    public void BuildMatcher_EscapesRegexMetacharactersInLiterals()
    {
        var matcher = McpResourceUriTemplate.BuildMatcher("calc://(1+2)*3/{x}");

        Assert.IsTrue(matcher.IsMatch("calc://(1+2)*3/y"));
        Assert.IsFalse(matcher.IsMatch("calc://1+2*3/y"));
    }

    [TestMethod]
    public void Resolve_PrefersAnExactResource_ThenTheFirstMatchingTemplate()
    {
        McpResourceDefinition[] declared =
        [
            new() { Uri = "docs://files/{name}", Name = "files", IsTemplate = true },
            new() { Uri = "docs://files/readme.md", Name = "readme" },
            new() { Uri = "docs://{+anything}", Name = "anything", IsTemplate = true },
        ];

        Assert.AreEqual("readme", McpResourceUriTemplate.Resolve(declared, "docs://files/readme.md")?.Name);
        Assert.AreEqual("files", McpResourceUriTemplate.Resolve(declared, "docs://files/other.md")?.Name);
        Assert.AreEqual("anything", McpResourceUriTemplate.Resolve(declared, "docs://deep/path")?.Name);
        Assert.IsNull(McpResourceUriTemplate.Resolve(declared, "other://x"));
    }

    [TestMethod]
    public void Resolve_DoesNotTreatAResourceUriAsATemplate()
    {
        // A plain resource whose URI happens to contain braces is compared exactly.
        McpResourceDefinition[] declared = [new() { Uri = "docs://{literal}", Name = "literal" }];

        Assert.IsNotNull(McpResourceUriTemplate.Resolve(declared, "docs://{literal}"));
        Assert.IsNull(McpResourceUriTemplate.Resolve(declared, "docs://x"));
    }

    [TestMethod]
    public void DescribeUnknownResource_ListsEveryDeclaredUri_AndKeepsTheServersError()
    {
        McpResourceDefinition[] declared =
        [
            new() { Uri = "docs://readme", Name = "readme" },
            new() { Uri = "docs://files/{name}", Name = "files", IsTemplate = true },
        ];

        var hint = McpCallDiagnostics.DescribeUnknownResource("docs", "docs://nope", declared, "Resource not found");

        StringAssert.Contains(hint, "Unknown resource 'docs://nope' on server 'docs'");
        StringAssert.Contains(hint, "[docs://files/{name}, docs://readme]");
        StringAssert.Contains(hint, "mcp_list_resources(server_name: \"docs\")");
        StringAssert.Contains(hint, "Underlying error: Resource not found");
    }

    [TestMethod]
    public void DescribeUnknownResource_CapsALongList()
    {
        var declared = Enumerable.Range(0, 60)
            .Select(i => new McpResourceDefinition { Uri = $"docs://r{i:D2}", Name = $"r{i}" })
            .ToList();

        var hint = McpCallDiagnostics.DescribeUnknownResource("docs", "docs://nope", declared, null);

        StringAssert.Contains(hint, "docs://r49]");
        StringAssert.Contains(hint, " and 10 more.");
        Assert.IsFalse(hint.Contains("docs://r50"));
        Assert.IsFalse(hint.Contains("Underlying error"));
    }
}
