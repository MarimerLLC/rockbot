using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class DataProtectionSetupTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-dp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static ServiceProvider BuildProvider(string? keyRingPath)
    {
        var settings = new Dictionary<string, string?>();
        if (keyRingPath is not null)
            settings[DataProtectionSetup.KeyRingPathKey] = keyRingPath;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotDataProtection(configuration);
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void ProtectedPayload_SurvivesProcessRestart()
    {
        var keyRing = Path.Combine(_root, "keys");
        const string payload = "antiforgery-token-stand-in";

        string ciphertext;
        using (var first = BuildProvider(keyRing))
        {
            ciphertext = first.GetDataProtector("test-purpose").Protect(payload);
        }

        // A second provider over the same directory is the closest in-process stand-in for a
        // restarted pod: nothing is shared but the files on disk.
        using var second = BuildProvider(keyRing);
        var roundTripped = second.GetDataProtector("test-purpose").Unprotect(ciphertext);

        Assert.AreEqual(payload, roundTripped);
    }

    [TestMethod]
    public void KeyFiles_AreWrittenToConfiguredPath()
    {
        var keyRing = Path.Combine(_root, "keys");

        using var provider = BuildProvider(keyRing);
        provider.GetDataProtector("test-purpose").Protect("x");

        var keyFiles = Directory.GetFiles(keyRing, "key-*.xml");
        Assert.IsTrue(keyFiles.Length > 0, $"expected a key XML file under {keyRing}");
    }

    [TestMethod]
    public void UnwritablePath_ThrowsNamingThePath()
    {
        // An existing file where the directory should be: CreateDirectory cannot succeed.
        var blocked = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocked, "occupied");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => BuildProvider(blocked));

        StringAssert.Contains(ex.Message, "not-a-directory");
    }

    [TestMethod]
    public void EmptyConfiguration_LeavesDefaultsAloneAndCreatesNothing()
    {
        using var provider = BuildProvider(null);

        // Registration is a no-op: no directory is probed or created, and nothing throws. The
        // real app is left on the ASP.NET Core defaults, which already persist on a dev machine.
        Assert.AreEqual(0, Directory.GetFileSystemEntries(_root).Length);
    }

    [TestMethod]
    public void WhitespaceConfiguration_IsTreatedAsUnset()
    {
        using var provider = BuildProvider("   ");

        Assert.AreEqual(0, Directory.GetFileSystemEntries(_root).Length);
    }
}
