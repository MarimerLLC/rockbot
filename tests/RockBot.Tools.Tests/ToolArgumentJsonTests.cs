using System.Text.Json;

namespace RockBot.Tools.Tests;

[TestClass]
public class ToolArgumentJsonTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement StringOf(string text) => Parse(JsonSerializer.Serialize(text));

    [TestMethod]
    public void UnwrapStringified_StringifiedArray_ReturnsArray()
    {
        var result = ToolArgumentJson.UnwrapStringified(StringOf("""[{"description":"a"},{"description":"b"}]"""));

        Assert.AreEqual(JsonValueKind.Array, result.ValueKind);
        Assert.AreEqual(2, result.GetArrayLength());
        Assert.AreEqual("b", result[1].GetProperty("description").GetString());
    }

    [TestMethod]
    public void UnwrapStringified_StringifiedObject_ReturnsObject()
    {
        var result = ToolArgumentJson.UnwrapStringified(StringOf("""{"id":"x","count":3}"""));

        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.AreEqual(3, result.GetProperty("count").GetInt32());
    }

    [TestMethod]
    public void UnwrapStringified_LeadingWhitespace_StillUnwraps()
    {
        var result = ToolArgumentJson.UnwrapStringified(StringOf("  \n\t[1, 2, 3]  "));

        Assert.AreEqual(JsonValueKind.Array, result.ValueKind);
        Assert.AreEqual(3, result.GetArrayLength());
    }

    [TestMethod]
    public void UnwrapStringified_DoubleEncodedArray_ReturnsArray()
    {
        // Seen live: a JSON string holding a JSON string holding the array.
        var once = """[{"description":"a"}]""";
        var twice = JsonSerializer.Serialize(once);

        var result = ToolArgumentJson.UnwrapStringified(StringOf(twice));

        Assert.AreEqual(JsonValueKind.Array, result.ValueKind);
        Assert.AreEqual("a", result[0].GetProperty("description").GetString());
    }

    [TestMethod]
    public void UnwrapStringified_TripleEncodedObject_ReturnsObject()
    {
        var thrice = JsonSerializer.Serialize(JsonSerializer.Serialize("""{"id":"x"}"""));

        var result = ToolArgumentJson.UnwrapStringified(StringOf(thrice));

        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.AreEqual("x", result.GetProperty("id").GetString());
    }

    [TestMethod]
    public void UnwrapStringified_EncodedDeeperThanTheLimit_Unchanged()
    {
        var text = """[1]""";
        for (var i = 0; i < 3; i++)
            text = JsonSerializer.Serialize(text);

        var original = StringOf(text);
        var result = ToolArgumentJson.UnwrapStringified(original);

        Assert.AreEqual(JsonValueKind.String, result.ValueKind);
        Assert.AreEqual(text, result.GetString());
    }

    [TestMethod]
    public void UnwrapStringified_QuotedPlainText_Unchanged()
    {
        // A string that is itself a JSON string literal, but holds no array or object.
        var result = ToolArgumentJson.UnwrapStringified(StringOf("\"hello\""));

        Assert.AreEqual(JsonValueKind.String, result.ValueKind);
        Assert.AreEqual("\"hello\"", result.GetString());
    }

    [TestMethod]
    public void UnwrapStringified_PlainString_Unchanged()
    {
        var result = ToolArgumentJson.UnwrapStringified(StringOf("just some text"));

        Assert.AreEqual(JsonValueKind.String, result.ValueKind);
        Assert.AreEqual("just some text", result.GetString());
    }

    [TestMethod]
    public void UnwrapStringified_EmptyString_Unchanged()
    {
        var result = ToolArgumentJson.UnwrapStringified(StringOf(""));

        Assert.AreEqual(JsonValueKind.String, result.ValueKind);
    }

    [TestMethod]
    public void UnwrapStringified_MalformedJson_Unchanged()
    {
        var result = ToolArgumentJson.UnwrapStringified(StringOf("[oops"));

        Assert.AreEqual(JsonValueKind.String, result.ValueKind);
        Assert.AreEqual("[oops", result.GetString());
    }

    [TestMethod]
    public void UnwrapStringified_NonString_Unchanged()
    {
        Assert.AreEqual(JsonValueKind.Array, ToolArgumentJson.UnwrapStringified(Parse("[1]")).ValueKind);
        Assert.AreEqual(JsonValueKind.Object, ToolArgumentJson.UnwrapStringified(Parse("{}")).ValueKind);
        Assert.AreEqual(JsonValueKind.Number, ToolArgumentJson.UnwrapStringified(Parse("42")).ValueKind);
        Assert.AreEqual(JsonValueKind.Null, ToolArgumentJson.UnwrapStringified(Parse("null")).ValueKind);
        Assert.AreEqual(JsonValueKind.Undefined, ToolArgumentJson.UnwrapStringified(default).ValueKind);
    }
}
