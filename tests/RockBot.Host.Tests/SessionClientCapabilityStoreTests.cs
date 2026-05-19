using RockBot.UserProxy;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class SessionClientCapabilityStoreTests
{
    [TestMethod]
    public void Get_ReturnsNone_WhenSessionUnknown()
    {
        var store = new SessionClientCapabilityStore();

        Assert.AreEqual(ClientCapabilities.None, store.Get("unseen-session"));
    }

    [TestMethod]
    public void Set_Then_Get_ReturnsStoredValue()
    {
        var store = new SessionClientCapabilityStore();

        store.Set("s1", ClientCapabilityPresets.Blazor);

        Assert.AreEqual(ClientCapabilityPresets.Blazor, store.Get("s1"));
    }

    [TestMethod]
    public void Set_LastWriterWins()
    {
        var store = new SessionClientCapabilityStore();
        store.Set("s1", ClientCapabilityPresets.Cli);

        store.Set("s1", ClientCapabilityPresets.Blazor);

        Assert.AreEqual(ClientCapabilityPresets.Blazor, store.Get("s1"));
    }

    [TestMethod]
    public void Set_None_RemovesEntry()
    {
        var store = new SessionClientCapabilityStore();
        store.Set("s1", ClientCapabilityPresets.Blazor);

        store.Set("s1", ClientCapabilities.None);

        // Missing entry and None-entry behave identically per the store invariant.
        Assert.AreEqual(ClientCapabilities.None, store.Get("s1"));
    }

    [TestMethod]
    public void Clear_RemovesEntry()
    {
        var store = new SessionClientCapabilityStore();
        store.Set("s1", ClientCapabilityPresets.Blazor);

        store.Clear("s1");

        Assert.AreEqual(ClientCapabilities.None, store.Get("s1"));
    }

    [TestMethod]
    public void Clear_UnknownSession_DoesNotThrow()
    {
        var store = new SessionClientCapabilityStore();

        store.Clear("never-set");

        Assert.AreEqual(ClientCapabilities.None, store.Get("never-set"));
    }

    [TestMethod]
    public void SessionIds_AreCaseInsensitive()
    {
        var store = new SessionClientCapabilityStore();
        store.Set("Session-A", ClientCapabilityPresets.Blazor);

        Assert.AreEqual(ClientCapabilityPresets.Blazor, store.Get("session-a"));
        Assert.AreEqual(ClientCapabilityPresets.Blazor, store.Get("SESSION-A"));
    }

    [TestMethod]
    public void DistinctSessions_AreIsolated()
    {
        var store = new SessionClientCapabilityStore();

        store.Set("a", ClientCapabilityPresets.Cli);
        store.Set("b", ClientCapabilityPresets.Blazor);

        Assert.AreEqual(ClientCapabilityPresets.Cli, store.Get("a"));
        Assert.AreEqual(ClientCapabilityPresets.Blazor, store.Get("b"));
    }
}
