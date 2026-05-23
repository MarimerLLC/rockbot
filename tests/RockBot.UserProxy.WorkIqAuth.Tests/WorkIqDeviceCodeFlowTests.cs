using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.UserProxy.WorkIqAuth.Tests;

[TestClass]
public class WorkIqDeviceCodeFlowTests
{
    [TestMethod]
    public async Task BeginAsync_NotConfigured_Throws()
    {
        var flow = new WorkIqDeviceCodeFlow(
            Options.Create(new WorkIqClientSettings()),
            new StubMessagePublisher(),
            NullLogger<WorkIqDeviceCodeFlow>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<WorkIqAuthFlowException>(
            () => flow.BeginAsync(CancellationToken.None));

        Assert.AreEqual(WorkIqAuthFlowException.Codes.NotConfigured, ex.Code);
        StringAssert.Contains(ex.Message, "WorkIQ:TenantId");
    }

    [TestMethod]
    public void Settings_Enabled_RequiresBothTenantAndClient()
    {
        Assert.IsFalse(new WorkIqClientSettings().Enabled);
        Assert.IsFalse(new WorkIqClientSettings { TenantId = "t" }.Enabled);
        Assert.IsFalse(new WorkIqClientSettings { ClientId = "c" }.Enabled);
        Assert.IsTrue(new WorkIqClientSettings { TenantId = "t", ClientId = "c" }.Enabled);
    }
}
