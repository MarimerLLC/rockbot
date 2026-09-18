using System.Text.Json;
using ModelContextProtocol.Protocol;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Tools.Tests.Elicitation;

[TestClass]
public class McpElicitationSchemaValidatorTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static ElicitRequestParams.RequestSchema Schema(
        IEnumerable<string>? required,
        params (string Name, ElicitRequestParams.PrimitiveSchemaDefinition Definition)[] fields)
    {
        var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();
        foreach (var (name, definition) in fields)
            properties[name] = definition;

        return new ElicitRequestParams.RequestSchema
        {
            Properties = properties,
            Required = required?.ToList(),
        };
    }

    [TestMethod]
    public void Validate_AcceptsValuesThatFitTheSchema()
    {
        var schema = Schema(
            ["mailbox"],
            ("mailbox", new ElicitRequestParams.StringSchema()),
            ("limit", new ElicitRequestParams.NumberSchema { Type = "integer", Minimum = 1, Maximum = 50 }),
            ("includeArchived", new ElicitRequestParams.BooleanSchema()));

        var result = McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["mailbox"] = Json("\"work\""),
            ["limit"] = Json("10"),
            ["includeArchived"] = Json("false"),
        });

        Assert.IsTrue(result.IsValid, string.Join("; ", result.Errors));
        Assert.AreEqual(3, result.Content.Count);
    }

    [TestMethod]
    public void Validate_RejectsAStringWhereANumberWasAskedFor()
    {
        var schema = Schema(null, ("limit", new ElicitRequestParams.NumberSchema()));

        var result = McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["limit"] = Json("\"10\""),
        });

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Errors[0], "limit");
    }

    [TestMethod]
    public void Validate_RejectsAFractionForAnIntegerField()
    {
        var schema = Schema(null, ("limit", new ElicitRequestParams.NumberSchema { Type = "integer" }));

        var result = McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["limit"] = Json("1.5"),
        });

        Assert.IsFalse(result.IsValid);
    }

    [TestMethod]
    public void Validate_EnforcesNumericRange()
    {
        var schema = Schema(null, ("limit", new ElicitRequestParams.NumberSchema { Minimum = 1, Maximum = 5 }));

        var tooBig = McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["limit"] = Json("9"),
        });

        Assert.IsFalse(tooBig.IsValid);
    }

    [TestMethod]
    public void Validate_EnforcesStringLength()
    {
        var schema = Schema(null, ("code", new ElicitRequestParams.StringSchema { MinLength = 3, MaxLength = 4 }));

        Assert.IsFalse(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["code"] = Json("\"ab\""),
        }).IsValid);

        Assert.IsTrue(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["code"] = Json("\"abcd\""),
        }).IsValid);
    }

    [TestMethod]
    public void Validate_EnforcesEnumMembership()
    {
        var schema = Schema(null, ("folder", new ElicitRequestParams.UntitledSingleSelectEnumSchema
        {
            Enum = ["inbox", "archive"],
        }));

        Assert.IsFalse(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["folder"] = Json("\"trash\""),
        }).IsValid);

        Assert.IsTrue(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["folder"] = Json("\"archive\""),
        }).IsValid);
    }

    [TestMethod]
    public void Validate_EnforcesTitledEnumMembershipOnTheConstNotTheTitle()
    {
        var schema = Schema(null, ("folder", new ElicitRequestParams.TitledSingleSelectEnumSchema
        {
            OneOf =
            [
                new ElicitRequestParams.EnumSchemaOption { Const = "inbox", Title = "Inbox" },
            ],
        }));

        Assert.IsFalse(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["folder"] = Json("\"Inbox\""),
        }).IsValid);

        Assert.IsTrue(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["folder"] = Json("\"inbox\""),
        }).IsValid);
    }

    [TestMethod]
    public void Validate_EnforcesMultiSelectMembershipAndBounds()
    {
        var schema = Schema(null, ("folders", new ElicitRequestParams.UntitledMultiSelectEnumSchema
        {
            Items = new ElicitRequestParams.UntitledEnumItemsSchema { Enum = ["inbox", "archive"] },
            MinItems = 1,
            MaxItems = 2,
        }));

        Assert.IsFalse(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["folders"] = Json("""["inbox","trash"]"""),
        }).IsValid);

        Assert.IsFalse(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["folders"] = Json("[]"),
        }).IsValid);

        Assert.IsTrue(McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["folders"] = Json("""["inbox"]"""),
        }).IsValid);
    }

    [TestMethod]
    public void Validate_ReportsMissingRequiredFields()
    {
        var schema = Schema(["mailbox"], ("mailbox", new ElicitRequestParams.StringSchema()));

        var result = McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>());

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Errors[0], "mailbox");
    }

    [TestMethod]
    public void Validate_DropsFieldsTheServerNeverAskedFor()
    {
        var schema = Schema(null, ("mailbox", new ElicitRequestParams.StringSchema()));

        var result = McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["mailbox"] = Json("\"work\""),
            ["invented"] = Json("\"nonsense\""),
        });

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.Content.ContainsKey("invented"));
        CollectionAssert.AreEquivalent(new[] { "invented" }, result.IgnoredFields.ToArray());
    }

    [TestMethod]
    public void Validate_LeavesOptionalFieldsOutSoSdkDefaultsStillApply()
    {
        var schema = Schema(
            null,
            ("mailbox", new ElicitRequestParams.StringSchema()),
            ("limit", new ElicitRequestParams.NumberSchema { Default = 25 }));

        var result = McpElicitationSchemaValidator.Validate(schema, new Dictionary<string, JsonElement>
        {
            ["mailbox"] = Json("\"work\""),
        });

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.Content.ContainsKey("limit"));
    }

    [TestMethod]
    public void Validate_WithNoSchema_AcceptsNothingAndIgnoresEverything()
    {
        var result = McpElicitationSchemaValidator.Validate(null, new Dictionary<string, JsonElement>
        {
            ["anything"] = Json("\"at all\""),
        });

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.Content.Count);
        CollectionAssert.AreEquivalent(new[] { "anything" }, result.IgnoredFields.ToArray());
    }
}
