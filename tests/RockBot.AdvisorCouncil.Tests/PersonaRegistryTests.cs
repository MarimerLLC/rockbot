using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;

namespace RockBot.AdvisorCouncil.Tests;

[TestClass]
public class PersonaRegistryTests
{
    [TestMethod]
    public void ParsePersonaContent_HappyPath_ReturnsPersonaWithBody()
    {
        var content =
            """
            ---
            id: skeptic
            name: The Skeptic
            description: Challenges assumptions
            default_research: false
            ---

            You are the Skeptic. Surface what could go wrong.
            """;

        var p = PersonaRegistry.ParsePersonaContent(content, "skeptic");

        Assert.IsNotNull(p);
        Assert.AreEqual("skeptic", p!.Id);
        Assert.AreEqual("The Skeptic", p.Name);
        Assert.AreEqual("Challenges assumptions", p.Description);
        Assert.IsFalse(p.DefaultResearch);
        StringAssert.Contains(p.SystemPrompt, "Surface what could go wrong");
    }

    [TestMethod]
    public void ParsePersonaContent_DefaultResearchTrue_IsParsed()
    {
        var content =
            """
            ---
            id: engineer
            default_research: true
            ---
            Body.
            """;

        var p = PersonaRegistry.ParsePersonaContent(content, "engineer");

        Assert.IsNotNull(p);
        Assert.IsTrue(p!.DefaultResearch);
    }

    [TestMethod]
    public void ParsePersonaContent_NoFrontmatter_ReturnsNull()
    {
        var content = "Just a body with no frontmatter at all.";
        var p = PersonaRegistry.ParsePersonaContent(content, "x");
        Assert.IsNull(p);
    }

    [TestMethod]
    public void ParsePersonaContent_FrontmatterButEmptyBody_ReturnsNull()
    {
        var content =
            """
            ---
            id: x
            ---

            """;
        var p = PersonaRegistry.ParsePersonaContent(content, "x");
        Assert.IsNull(p);
    }

    [TestMethod]
    public void ParsePersonaContent_FilenameUsedWhenIdMissing()
    {
        var content =
            """
            ---
            name: Fallback
            ---
            Body.
            """;
        var p = PersonaRegistry.ParsePersonaContent(content, "default_filename");
        Assert.IsNotNull(p);
        Assert.AreEqual("default_filename", p!.Id);
    }

    [TestMethod]
    public void ComputeHash_IsStable_OrderInsensitive()
    {
        var a = new Dictionary<string, Persona>
        {
            ["a"] = new("a", "A", "d", "prompt a", false),
            ["b"] = new("b", "B", "d", "prompt b", false)
        };
        var b = new Dictionary<string, Persona>
        {
            ["b"] = new("b", "B", "d", "prompt b", false),
            ["a"] = new("a", "A", "d", "prompt a", false)
        };
        Assert.AreEqual(PersonaRegistry.ComputeHash(a), PersonaRegistry.ComputeHash(b));
    }

    [TestMethod]
    public void ComputeHash_ChangesWhenSystemPromptChanges()
    {
        var a = new Dictionary<string, Persona>
        {
            ["a"] = new("a", "A", "d", "prompt a", false)
        };
        var b = new Dictionary<string, Persona>
        {
            ["a"] = new("a", "A", "d", "prompt a-modified", false)
        };
        Assert.AreNotEqual(PersonaRegistry.ComputeHash(a), PersonaRegistry.ComputeHash(b));
    }

    [TestMethod]
    public void Registry_LoadsPersonasFromDirectory()
    {
        var tmp = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "skeptic.md"),
                "---\nid: skeptic\nname: Skeptic\n---\nSkeptic body.");
            File.WriteAllText(Path.Combine(tmp, "engineer.md"),
                "---\nid: engineer\ndefault_research: true\n---\nEngineer body.");

            var opts = Options.Create(new CouncilOptions { PersonasPath = tmp });
            var reg = new PersonaRegistry(opts, NullLogger<PersonaRegistry>.Instance);

            Assert.AreEqual(2, reg.Personas.Count);
            Assert.IsTrue(reg.Personas.ContainsKey("skeptic"));
            Assert.IsTrue(reg.Personas.ContainsKey("engineer"));
            Assert.IsTrue(reg.Personas["engineer"].DefaultResearch);
            Assert.IsFalse(string.IsNullOrEmpty(reg.PersonaSetHash));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [TestMethod]
    public void Registry_Reload_PicksUpNewFiles()
    {
        var tmp = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "skeptic.md"),
                "---\nid: skeptic\n---\nSkeptic body.");

            var opts = Options.Create(new CouncilOptions { PersonasPath = tmp });
            var reg = new PersonaRegistry(opts, NullLogger<PersonaRegistry>.Instance);
            var hashBefore = reg.PersonaSetHash;
            Assert.AreEqual(1, reg.Personas.Count);

            File.WriteAllText(Path.Combine(tmp, "engineer.md"),
                "---\nid: engineer\n---\nEngineer body.");
            reg.Reload();

            Assert.AreEqual(2, reg.Personas.Count);
            Assert.AreNotEqual(hashBefore, reg.PersonaSetHash);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "advisor-council-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
