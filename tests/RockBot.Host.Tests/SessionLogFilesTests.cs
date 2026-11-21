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
    public void TildePercentAndBackslash_AreEscaped() =>
        Assert.AreEqual("a%5Cb%7Ec%25d", SessionLogFiles.StemFor("a\\b~c%d"));

    [TestMethod]
    public void DistinctIds_NeverShareAStem()
    {
        string[] ids = ["patrol/inbox", "patrol~inbox", "patrol\\inbox", "patrol%7Einbox", "a/~b", "a~/b", "", "%00"];

        var stems = ids.Select(SessionLogFiles.StemFor).ToList();

        Assert.AreEqual(ids.Length, stems.Distinct(StringComparer.Ordinal).Count(), string.Join(", ", stems));
    }

    [TestMethod]
    public void JsonlPath_IsDirectlyUnderTheDirectory()
    {
        var path = SessionLogFiles.JsonlPath("/data/log", "patrol/x");
        Assert.AreEqual(Path.GetFullPath("/data/log"), Path.GetDirectoryName(Path.GetFullPath(path)));
        Assert.AreEqual("patrol~x.jsonl", Path.GetFileName(path));
    }
}
