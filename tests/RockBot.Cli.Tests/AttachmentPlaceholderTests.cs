using RockBot.UserProxy;
using RockBot.UserProxy.Cli;

namespace RockBot.Cli.Tests;

[TestClass]
public sealed class AttachmentPlaceholderTests
{
    [TestMethod]
    public void Render_Image_UsesImageLabelAndFriendlyName()
    {
        var att = new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "Q3 chart.png" };

        Assert.AreEqual("[image: Q3 chart.png (image/png)]", AttachmentPlaceholder.Render(att));
    }

    [TestMethod]
    public void Render_Image_FallsBackToPath_WhenNoFileName()
    {
        var att = new AgentAttachment { Mime = "image/jpeg", Path = "photo.jpg" };

        Assert.AreEqual("[image: photo.jpg (image/jpeg)]", AttachmentPlaceholder.Render(att));
    }

    [TestMethod]
    public void Render_NonImage_UsesAttachmentLabel()
    {
        var att = new AgentAttachment { Mime = "application/pdf", Path = "report.pdf" };

        Assert.AreEqual("[attachment: report.pdf (application/pdf)]", AttachmentPlaceholder.Render(att));
    }
}
