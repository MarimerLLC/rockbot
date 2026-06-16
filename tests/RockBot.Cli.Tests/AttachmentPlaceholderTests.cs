using RockBot.UserProxy;
using RockBot.UserProxy.Cli;

namespace RockBot.Cli.Tests;

[TestClass]
public sealed class AttachmentPlaceholderTests
{
    [TestMethod]
    public void Render_Image_ShowsFriendlyNameAndPath_WhenTheyDiffer()
    {
        var att = new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "Q3 chart.png" };

        // The path is always surfaced so it can be inspected/tested; the friendly name leads.
        Assert.AreEqual("[image: Q3 chart.png (chart.png, image/png)]", AttachmentPlaceholder.Render(att));
    }

    [TestMethod]
    public void Render_Image_UsesPathOnly_WhenNoFileName()
    {
        var att = new AgentAttachment { Mime = "image/jpeg", Path = "photo.jpg" };

        Assert.AreEqual("[image: photo.jpg (image/jpeg)]", AttachmentPlaceholder.Render(att));
    }

    [TestMethod]
    public void Render_Image_UsesPathOnly_WhenFriendlyNameEqualsPath()
    {
        var att = new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "chart.png" };

        // No redundant duplication when the friendly name is just the path.
        Assert.AreEqual("[image: chart.png (image/png)]", AttachmentPlaceholder.Render(att));
    }

    [TestMethod]
    public void Render_NonImage_UsesAttachmentLabel()
    {
        var att = new AgentAttachment { Mime = "application/pdf", Path = "report.pdf" };

        Assert.AreEqual("[attachment: report.pdf (application/pdf)]", AttachmentPlaceholder.Render(att));
    }
}
