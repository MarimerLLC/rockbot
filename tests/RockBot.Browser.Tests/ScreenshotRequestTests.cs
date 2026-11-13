namespace RockBot.Browser.Tests;

/// <summary>What <c>POST /screenshot</c> accepts, checked without launching a browser.</summary>
[TestClass]
public class ScreenshotRequestTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Validate_RejectsAMissingDocument(string? html)
    {
        Assert.IsNotNull(ScreenshotRequest.Validate(new ScreenshotRequest(html, null, null, null)));
    }

    [TestMethod]
    public void Validate_RejectsANullBody()
    {
        Assert.IsNotNull(ScreenshotRequest.Validate(null));
    }

    [TestMethod]
    public void Validate_RejectsAnOversizeDocument()
    {
        var html = new string('x', ScreenshotRequest.MaxHtmlChars + 1);

        var error = ScreenshotRequest.Validate(new ScreenshotRequest(html, null, null, null));

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "limit");
    }

    [TestMethod]
    public void Validate_AcceptsAnOrdinaryDocument()
    {
        Assert.IsNull(ScreenshotRequest.Validate(new ScreenshotRequest("<html></html>", null, null, null)));
    }

    [TestMethod]
    public void Viewport_DefaultsToASixteenByNineSlide()
    {
        var viewport = ScreenshotRequest.Viewport(new ScreenshotRequest("<html/>", null, null, null));

        Assert.AreEqual((1280, 720), viewport);
    }

    [TestMethod]
    [DataRow(1920, 1080, 1920, 1080)]
    [DataRow(1, 1, ScreenshotRequest.MinWidth, ScreenshotRequest.MinHeight)]
    [DataRow(100_000, 100_000, ScreenshotRequest.MaxWidth, ScreenshotRequest.MaxHeight)]
    [DataRow(-5, 0, ScreenshotRequest.MinWidth, ScreenshotRequest.MinHeight)]
    public void Viewport_IsHeldWithinBounds(int width, int height, int expectedWidth, int expectedHeight)
    {
        // A huge viewport is a huge bitmap in the sidecar's memory.
        var viewport = ScreenshotRequest.Viewport(new ScreenshotRequest("<html/>", width, height, null));

        Assert.AreEqual((expectedWidth, expectedHeight), viewport);
    }

    [TestMethod]
    public void Result_SeparatesABadRequestFromAFailedPage()
    {
        Assert.IsTrue(ScreenshotResult.Rejected("no html").BadRequest);
        Assert.IsFalse(ScreenshotResult.Failed("timeout").BadRequest);
        Assert.IsNull(ScreenshotResult.Failed("timeout").Png);
        Assert.AreEqual(3, ScreenshotResult.Captured([1], 3).BlockedRequests);
    }
}
