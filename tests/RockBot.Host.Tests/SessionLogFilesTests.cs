namespace RockBot.Host.Tests;

[TestClass]
public class SessionLogFilesTests
{
    [TestMethod]
    [DataRow("blazor-session")]
    [DataRow("subagent-3f2a")]
    [DataRow("cli-decision-1794752232")]
    public void PlainIds_KeepTheirExistingFileName(string id) =>
        Assert.AreEqual(id, SessionLogFiles.StemFor(id));

    [TestMethod]
    public void PatrolIds_MapToAFlatFileName() =>
        Assert.AreEqual("patrol~concert-sweep", SessionLogFiles.StemFor("patrol/concert-sweep"));

    [TestMethod]
    public void BackslashesAlsoMapToTilde() =>
        Assert.AreEqual("a~b", SessionLogFiles.StemFor("a\\b"));

    [TestMethod]
    public void JsonlPath_IsDirectlyUnderTheDirectory()
    {
        var path = SessionLogFiles.JsonlPath("/data/log", "patrol/x");
        Assert.AreEqual(Path.GetFullPath("/data/log"), Path.GetDirectoryName(Path.GetFullPath(path)));
        Assert.AreEqual("patrol~x.jsonl", Path.GetFileName(path));
    }
}
