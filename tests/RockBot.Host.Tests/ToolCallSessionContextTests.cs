namespace RockBot.Host.Tests;

[TestClass]
public class ToolCallSessionContextTests
{
    [TestMethod]
    public void SessionId_DefaultsToNull()
    {
        Assert.IsNull(ToolCallSessionContext.SessionId);
    }

    [TestMethod]
    public void Set_SetsSessionId()
    {
        using var scope = ToolCallSessionContext.Set("test-session");
        Assert.AreEqual("test-session", ToolCallSessionContext.SessionId);
    }

    [TestMethod]
    public void Dispose_RestoresPreviousValue()
    {
        using (ToolCallSessionContext.Set("outer"))
        {
            Assert.AreEqual("outer", ToolCallSessionContext.SessionId);

            using (ToolCallSessionContext.Set("inner"))
            {
                Assert.AreEqual("inner", ToolCallSessionContext.SessionId);
            }

            Assert.AreEqual("outer", ToolCallSessionContext.SessionId);
        }

        Assert.IsNull(ToolCallSessionContext.SessionId);
    }

    [TestMethod]
    public void Set_Null_ClearsSessionId()
    {
        using (ToolCallSessionContext.Set("session"))
        {
            Assert.AreEqual("session", ToolCallSessionContext.SessionId);

            using (ToolCallSessionContext.Set(null))
            {
                Assert.IsNull(ToolCallSessionContext.SessionId);
            }

            Assert.AreEqual("session", ToolCallSessionContext.SessionId);
        }
    }
}
