using System.Text.Json;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class StrictArgumentsTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "title": { "type": "string" }
          },
          "required": ["id"]
        }
        """).RootElement;

    [TestMethod]
    public void FindUnknownArguments_AllDeclared_ReturnsEmpty()
    {
        var unknown = StrictArguments.FindUnknownArguments(Schema, ["id", "title"]);

        Assert.AreEqual(0, unknown.Count);
    }

    [TestMethod]
    public void FindUnknownArguments_NoArguments_ReturnsEmpty()
    {
        var unknown = StrictArguments.FindUnknownArguments(Schema, []);

        Assert.AreEqual(0, unknown.Count);
    }

    [TestMethod]
    public void FindUnknownArguments_NamesEveryUnknownKey()
    {
        var unknown = StrictArguments.FindUnknownArguments(Schema, ["id", "recurrence", "dueDate"]);

        CollectionAssert.AreEqual(new[] { "recurrence", "dueDate" }, unknown.ToArray());
    }

    [TestMethod]
    public void FindUnknownArguments_IsCaseSensitive()
    {
        var unknown = StrictArguments.FindUnknownArguments(Schema, ["ID"]);

        CollectionAssert.AreEqual(new[] { "ID" }, unknown.ToArray());
    }

    [TestMethod]
    public void FindUnknownArguments_SchemaWithoutProperties_RejectsEverything()
    {
        var empty = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var unknown = StrictArguments.FindUnknownArguments(empty, ["anything"]);

        CollectionAssert.AreEqual(new[] { "anything" }, unknown.ToArray());
    }

    [TestMethod]
    public void DisallowAdditionalProperties_AddsFalseToRoot()
    {
        var result = StrictArguments.DisallowAdditionalProperties(Schema);

        Assert.IsFalse(result.GetProperty("additionalProperties").GetBoolean());
        Assert.IsTrue(result.GetProperty("properties").TryGetProperty("title", out _));
    }

    [TestMethod]
    public void DisallowAdditionalProperties_LeavesExistingSettingAlone()
    {
        var open = JsonDocument.Parse("""{ "type": "object", "additionalProperties": true }""").RootElement;

        var result = StrictArguments.DisallowAdditionalProperties(open);

        Assert.IsTrue(result.GetProperty("additionalProperties").GetBoolean());
    }

    [TestMethod]
    public void FindAllowedValues_ReturnsStringEnumMembers()
    {
        var schema = JsonDocument.Parse("""
            { "type": "object", "properties": { "kind": { "type": ["string", "null"], "enum": ["a", "b", null] } } }
            """).RootElement;

        CollectionAssert.AreEqual(new[] { "a", "b" }, StrictArguments.FindAllowedValues(schema, "kind")!.ToArray());
    }

    [TestMethod]
    public void FindAllowedValues_NonEnumProperty_ReturnsNull()
    {
        Assert.IsNull(StrictArguments.FindAllowedValues(Schema, "title"));
        Assert.IsNull(StrictArguments.FindAllowedValues(Schema, "missing"));
    }
}
