using System.Text.Json;

namespace RockBot.Tools.FileSystem.Tests;

[TestClass]
public class FileEditToolExecutorTests
{
    private string _root = null!;
    private FileSystemOptions _options = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-file-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _options = new FileSystemOptions { BasePath = _root };
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static ToolInvokeRequest Request(object args) => new()
    {
        ToolCallId = "call_1",
        ToolName = "file_edit",
        Arguments = JsonSerializer.Serialize(args)
    };

    private Task<ToolInvokeResponse> ExecuteAsync(object args) =>
        new FileEditToolExecutor(_options).ExecuteAsync(Request(args), CancellationToken.None);

    [TestMethod]
    public async Task ExecuteAsync_AppliesEditAndPersistsIt()
    {
        var full = WriteFile("canon/NPCs.md", "# NPCs\n\n**Georgie** — dock foreman, neutral\n");

        var response = await ExecuteAsync(new
        {
            path = "canon/NPCs.md",
            old_string = "dock foreman, neutral",
            new_string = "dock foreman, owes the crew a favour"
        });

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual(
            "# NPCs\n\n**Georgie** — dock foreman, owes the crew a favour\n",
            File.ReadAllText(full));
    }

    [TestMethod]
    public async Task ExecuteAsync_ReportsReplacementCountAndSizeDelta()
    {
        WriteFile("notes.md", "alpha beta gamma");

        var response = await ExecuteAsync(new
        {
            path = "notes.md",
            old_string = "beta",
            new_string = "b"
        });

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content!, "Replaced 1 occurrence");
        StringAssert.Contains(response.Content!, "16 → 13");
    }

    [TestMethod]
    public async Task ExecuteAsync_LeavesFileUntouched_WhenMatchIsAmbiguous()
    {
        const string original = "status: active\nstatus: active\n";
        var full = WriteFile("roster.md", original);

        var response = await ExecuteAsync(new
        {
            path = "roster.md",
            old_string = "status: active",
            new_string = "status: retired"
        });

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "2 times");
        Assert.AreEqual(original, File.ReadAllText(full), "an ambiguous edit must not modify the file");
    }

    [TestMethod]
    public async Task ExecuteAsync_LeavesFileUntouched_WhenOldStringNotFound()
    {
        const string original = "# Title\n\nbody\n";
        var full = WriteFile("doc.md", original);

        var response = await ExecuteAsync(new
        {
            path = "doc.md",
            old_string = "missing text",
            new_string = "replacement"
        });

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "not found");
        Assert.AreEqual(original, File.ReadAllText(full));
    }

    [TestMethod]
    public async Task ExecuteAsync_ReplacesAll_WhenReplaceAllIsTrue()
    {
        var full = WriteFile("roster.md", "status: active\nstatus: active\n");

        var response = await ExecuteAsync(new
        {
            path = "roster.md",
            old_string = "status: active",
            new_string = "status: retired",
            replace_all = true
        });

        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content!, "Replaced 2 occurrences");
        Assert.AreEqual("status: retired\nstatus: retired\n", File.ReadAllText(full));
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsError_WhenFileDoesNotExist()
    {
        var response = await ExecuteAsync(new
        {
            path = "nope.md",
            old_string = "a",
            new_string = "b"
        });

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "File not found");
        StringAssert.Contains(response.Content!, "file_write");
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsPathTraversal()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"rockbot-outside-{Guid.NewGuid():N}.md");
        File.WriteAllText(outside, "secret");

        try
        {
            var response = await ExecuteAsync(new
            {
                path = $"../{Path.GetFileName(outside)}",
                old_string = "secret",
                new_string = "leaked"
            });

            Assert.IsTrue(response.IsError);
            StringAssert.Contains(response.Content!, "Invalid path");
            Assert.AreEqual("secret", File.ReadAllText(outside), "traversal must not modify files outside the volume");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ReturnsError_WhenRequiredArgumentMissing()
    {
        WriteFile("doc.md", "body");

        var missingOld = await ExecuteAsync(new { path = "doc.md", new_string = "x" });
        Assert.IsTrue(missingOld.IsError);
        StringAssert.Contains(missingOld.Content!, "old_string");

        var missingNew = await ExecuteAsync(new { path = "doc.md", old_string = "body" });
        Assert.IsTrue(missingNew.IsError);
        StringAssert.Contains(missingNew.Content!, "new_string");

        var missingPath = await ExecuteAsync(new { old_string = "body", new_string = "x" });
        Assert.IsTrue(missingPath.IsError);
        StringAssert.Contains(missingPath.Content!, "path");
    }

    [TestMethod]
    public async Task ExecuteAsync_DeletesMatchedText_WhenNewStringIsEmpty()
    {
        var full = WriteFile("doc.md", "keep this\nDRAFT — remove me\nkeep that\n");

        var response = await ExecuteAsync(new
        {
            path = "doc.md",
            old_string = "DRAFT — remove me\n",
            new_string = ""
        });

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("keep this\nkeep that\n", File.ReadAllText(full));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsNullNewString_RatherThanDeleting()
    {
        const string original = "keep this text\n";
        var full = WriteFile("doc.md", original);

        var response = await ExecuteAsync(new
        {
            path = "doc.md",
            old_string = "keep this text",
            new_string = (string?)null
        });

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "new_string must be a string");
        Assert.AreEqual(original, File.ReadAllText(full),
            "a null new_string must not be treated as an intentional deletion");
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsNonStringArguments()
    {
        WriteFile("doc.md", "body");

        var nullOld = await ExecuteAsync(new { path = "doc.md", old_string = (string?)null, new_string = "x" });
        Assert.IsTrue(nullOld.IsError);
        StringAssert.Contains(nullOld.Content!, "old_string must be a string");

        var numericPath = await ExecuteAsync(new { path = 7, old_string = "body", new_string = "x" });
        Assert.IsTrue(numericPath.IsError);
        StringAssert.Contains(numericPath.Content!, "path must be a string");
    }

    [TestMethod]
    public async Task ExecuteAsync_AcceptsStringSpellingOfReplaceAll()
    {
        // The text-based tool-calling path has no schema to coerce "true" to true.
        var full = WriteFile("roster.md", "status: active\nstatus: active\n");

        var response = await ExecuteAsync(new
        {
            path = "roster.md",
            old_string = "status: active",
            new_string = "status: retired",
            replace_all = "true"
        });

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("status: retired\nstatus: retired\n", File.ReadAllText(full));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsUnparseableReplaceAll()
    {
        const string original = "status: active\nstatus: active\n";
        var full = WriteFile("roster.md", original);

        var response = await ExecuteAsync(new
        {
            path = "roster.md",
            old_string = "status: active",
            new_string = "status: retired",
            replace_all = "yes"
        });

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "replace_all must be true or false");
        Assert.AreEqual(original, File.ReadAllText(full));
    }

    [TestMethod]
    public async Task ExecuteAsync_TreatsAbsentAndFalseReplaceAllAlike()
    {
        WriteFile("roster.md", "status: active\nstatus: active\n");

        var explicitFalse = await ExecuteAsync(new
        {
            path = "roster.md",
            old_string = "status: active",
            new_string = "status: retired",
            replace_all = false
        });

        Assert.IsTrue(explicitFalse.IsError);
        StringAssert.Contains(explicitFalse.Content!, "2 times");
    }

    [TestMethod]
    public async Task ExecuteAsync_AppliesBothEdits_WhenSameFileIsEditedConcurrently()
    {
        // Two subagents editing one document must not have the second read stale
        // content and overwrite the first — both edits belong in the result.
        var full = WriteFile("canon/NPCs.md", "**Georgie** — neutral\n**Wren** — neutral\n");

        var georgie = ExecuteAsync(new
        {
            path = "canon/NPCs.md",
            old_string = "**Georgie** — neutral",
            new_string = "**Georgie** — allied"
        });
        var wren = ExecuteAsync(new
        {
            path = "canon/NPCs.md",
            old_string = "**Wren** — neutral",
            new_string = "**Wren** — hostile"
        });

        var responses = await Task.WhenAll(georgie, wren);

        foreach (var response in responses)
            Assert.IsFalse(response.IsError, response.Content);

        Assert.AreEqual("**Georgie** — allied\n**Wren** — hostile\n", File.ReadAllText(full));
    }

    [TestMethod]
    public async Task ExecuteAsync_PreservesUnrelatedContentOfLargeFile()
    {
        var lines = Enumerable.Range(0, 500).Select(i => $"## Section {i}\nBody text for section {i}.\n");
        var original = string.Concat(lines);
        var full = WriteFile("canon/big.md", original);

        var response = await ExecuteAsync(new
        {
            path = "canon/big.md",
            old_string = "Body text for section 250.",
            new_string = "Rewritten body for section 250."
        });

        Assert.IsFalse(response.IsError, response.Content);
        var edited = File.ReadAllText(full);
        Assert.AreEqual(original.Replace("Body text for section 250.", "Rewritten body for section 250."), edited);
        Assert.AreEqual(original.Length + 5, edited.Length);
    }
}
