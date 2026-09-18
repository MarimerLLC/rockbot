using ModelContextProtocol.Protocol;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Tools.Tests.Elicitation;

[TestClass]
public class McpSensitiveFieldDetectorTests
{
    [DataTestMethod]
    [DataRow("password")]
    [DataRow("userPassword")]
    [DataRow("api_key")]
    [DataRow("apiKey")]
    [DataRow("APIKEY")]
    [DataRow("accessToken")]
    [DataRow("client_secret")]
    [DataRow("ssn")]
    [DataRow("creditCardNumber")]
    [DataRow("cvv")]
    [DataRow("otp")]
    public void IsSensitive_FlagsCredentialShapedNames(string field)
        => Assert.IsTrue(McpSensitiveFieldDetector.IsSensitive(field), field);

    [DataTestMethod]
    [DataRow("mailbox")]
    [DataRow("maxTokens")]
    [DataRow("slotPosition")]
    [DataRow("subject")]
    [DataRow("startDate")]
    [DataRow("laptopType")]
    public void IsSensitive_LeavesOrdinaryNamesAlone(string field)
        => Assert.IsFalse(McpSensitiveFieldDetector.IsSensitive(field), field);

    [TestMethod]
    public void IsSensitive_ReadsTheDescriptionToo()
    {
        var schema = new ElicitRequestParams.StringSchema
        {
            Description = "Your account password, so the server can sign in."
        };

        Assert.IsTrue(McpSensitiveFieldDetector.IsSensitive("value", schema));
    }

    [TestMethod]
    public void FindSensitiveFields_ReturnsOnlyTheOffendingFields()
    {
        var schema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                ["mailbox"] = new ElicitRequestParams.StringSchema(),
                ["apiKey"] = new ElicitRequestParams.StringSchema(),
            }
        };

        var hits = McpSensitiveFieldDetector.FindSensitiveFields(schema);

        CollectionAssert.AreEquivalent(new[] { "apiKey" }, hits.ToArray());
    }

    [TestMethod]
    public void FindSensitiveFields_HonoursTheConfiguredDenyList()
    {
        var schema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                ["mailbox"] = new ElicitRequestParams.StringSchema(),
            }
        };

        var hits = McpSensitiveFieldDetector.FindSensitiveFields(schema, ["MAILBOX"]);

        CollectionAssert.AreEquivalent(new[] { "mailbox" }, hits.ToArray());
    }

    [TestMethod]
    public void FindSensitiveFields_ReturnsEmptyForNoSchema()
        => Assert.AreEqual(0, McpSensitiveFieldDetector.FindSensitiveFields(null).Count);
}
