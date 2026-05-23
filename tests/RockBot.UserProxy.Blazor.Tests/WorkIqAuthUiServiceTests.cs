using RockBot.UserProxy.Blazor.Services;
using RockBot.UserProxy.WorkIqAuth;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class WorkIqAuthUiServiceTests
{
    [TestMethod]
    public void NoExpired_BannerHidden()
    {
        var listener = new FakeListener();
        var service = new WorkIqAuthUiService(listener);

        Assert.IsFalse(service.IsExpiredBannerVisible);
        Assert.IsNull(service.CurrentReason);
    }

    [TestMethod]
    public void Listener_ExpiredEvent_ShowsBannerAndNotifies()
    {
        var listener = new FakeListener();
        var service = new WorkIqAuthUiService(listener);
        var notified = 0;
        service.StateChanged += () => notified++;

        listener.FireExpired(new WorkIqAuthExpired
        {
            AccountId = "acct@example.com",
            Reason = "refresh revoked"
        });

        Assert.IsTrue(service.IsExpiredBannerVisible);
        Assert.AreEqual("refresh revoked", service.CurrentReason);
        Assert.AreEqual(1, notified);
    }

    [TestMethod]
    public void DismissExpiredBanner_HidesAndNotifies()
    {
        var listener = new FakeListener();
        var service = new WorkIqAuthUiService(listener);
        listener.FireExpired(new WorkIqAuthExpired { AccountId = "acct" });
        var notified = 0;
        service.StateChanged += () => notified++;

        service.DismissExpiredBanner();

        Assert.IsFalse(service.IsExpiredBannerVisible);
        Assert.AreEqual(1, notified);
    }

    [TestMethod]
    public void DismissExpiredBanner_WhenAlreadyHidden_DoesNotNotify()
    {
        var listener = new FakeListener();
        var service = new WorkIqAuthUiService(listener);
        var notified = 0;
        service.StateChanged += () => notified++;

        service.DismissExpiredBanner();

        Assert.AreEqual(0, notified);
    }

    [TestMethod]
    public void MarkConnected_ClearsListenerStateAndBanner()
    {
        var listener = new FakeListener();
        listener.SetLastExpired(new WorkIqAuthExpired { AccountId = "acct", Reason = "expired" });
        var service = new WorkIqAuthUiService(listener);
        // Constructor initialized CurrentReason from listener.LastExpired but didn't
        // show the banner; we want MarkConnected to scrub both fields cleanly.
        service.GetType(); // suppress unused warning, service is the SUT

        service.MarkConnected();

        Assert.IsNull(listener.LastExpired);
        Assert.IsFalse(service.IsExpiredBannerVisible);
        Assert.IsNull(service.CurrentReason);
    }

    [TestMethod]
    public void Constructor_PicksUpPriorExpiration()
    {
        var listener = new FakeListener();
        listener.SetLastExpired(new WorkIqAuthExpired
        {
            AccountId = "acct",
            Reason = "expired before circuit started"
        });

        var service = new WorkIqAuthUiService(listener);

        // Reason is restored; banner stays hidden until a new event fires
        // because the prior expiration may have already been dismissed by
        // another circuit.
        Assert.AreEqual("expired before circuit started", service.CurrentReason);
        Assert.IsFalse(service.IsExpiredBannerVisible);
    }

    [TestMethod]
    public void Dispose_UnsubscribesFromListener()
    {
        var listener = new FakeListener();
        var service = new WorkIqAuthUiService(listener);
        var notified = 0;
        service.StateChanged += () => notified++;

        service.Dispose();
        listener.FireExpired(new WorkIqAuthExpired { AccountId = "acct" });

        Assert.AreEqual(0, notified);
    }

    private sealed class FakeListener : IWorkIqAuthStatusListener
    {
        public event EventHandler<WorkIqAuthExpired>? Expired;
        public WorkIqAuthExpired? LastExpired { get; private set; }
        public void ClearLastExpired() => LastExpired = null;

        public void SetLastExpired(WorkIqAuthExpired msg) => LastExpired = msg;

        public void FireExpired(WorkIqAuthExpired msg)
        {
            LastExpired = msg;
            Expired?.Invoke(this, msg);
        }
    }
}
