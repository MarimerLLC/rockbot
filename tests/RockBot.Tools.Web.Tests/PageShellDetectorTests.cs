namespace RockBot.Tools.Web.Tests;

[TestClass]
public class PageShellDetectorTests
{
    // Shaped like the conference page behind #667: a hidden home-grown cookie banner,
    // and the real content nested in <article>/<section>/<main>.
    internal const string ArticleWrappedPage = """
        <!DOCTYPE html>
        <html><head><title>Jane Example | Conference</title>
        <div id="cookie-banner" hidden><p>We use cookies for analytics and marketing.</p>
          <button id="cookie-accept">Accept all</button><button id="cookie-reject">Reject all</button></div>
        </head>
        <body>
          <nav role="navigation"><a href="/schedule">Schedule</a></nav>
          <header class="header"><img src="/logo.png" alt="logo"></header>
          <article class="page"><div class="panel"><section class="speaker-detail">
            <main class="speaker-detail__content">
              <h2>Jane Example</h2>
              <div class="bio">Jane Example builds distributed systems and writes about agents, messaging and the craft of software.</div>
              <h3>Sessions</h3>
              <div class="session-cards">
                <a class="session-card" href="/sessions/1"><div>Room 13</div><div class="title">Building Durable Agents</div><div>Tue 27 Oct 13:15</div></a>
                <a class="session-card" href="/sessions/2"><div>Room 7</div><div class="title">Protocols for Tool Calling</div><div>Wed 28 Oct 09:45</div></a>
              </div>
            </main>
          </section></div></article>
        </body></html>
        """;

    private const string OneTrustShell = """
        <html><head><title>Schedule</title><script src="/a.js"></script></head>
        <body>
          <div id="onetrust-consent-sdk"><div id="onetrust-banner-sdk">
            <p>We and our partners use cookies to store and access information on your device. By clicking
            "Accept all" you agree to the storing of cookies to enhance site navigation, analyse site usage
            and assist in our marketing efforts. You can change your preferences at any time in the cookie
            settings. Read our cookie policy and privacy notice for details on the partners we work with.</p>
            <button id="onetrust-accept-btn-handler">Accept all</button>
            <button id="onetrust-reject-all-handler">Reject all</button>
          </div></div>
          <div id="root"></div>
        </body></html>
        """;

    private const string NextJsShell = """
        <html><head><title>App</title>
        <script src="/_next/static/chunks/main.js"></script>
        <script src="/_next/static/chunks/app.js"></script></head>
        <body><div id="__next"></div><noscript>You need to enable JavaScript to run this app.</noscript></body></html>
        """;

    private const string LongArticleWithBanner = """
        <html><head><title>Article</title></head>
        <body>
          <div class="cookie-banner">We use cookies. <button>Accept</button></div>
          <main><h1>Release notes</h1>
          <p>This release adds streaming responses, a new retry policy for transient failures, and a
          rewritten configuration binder that reports unknown keys at startup instead of ignoring them.</p>
          <p>The retry policy backs off exponentially with jitter, honours Retry-After headers, and caps the
          total wait so that a single call cannot stall a request pipeline for minutes on end.</p></main>
        </body></html>
        """;

    private const string EmptyPage = "<html><head><title>T</title></head><body><p>OK</p></body></html>";

    [TestMethod]
    public async Task Convert_KeepsContentNestedInMainArticleAndSection()
    {
        var page = await HtmlPageConverter.ConvertAsync(ArticleWrappedPage, 200, CancellationToken.None);

        StringAssert.Contains(page.Markdown, "Building Durable Agents");
        StringAssert.Contains(page.Markdown, "Room 13");
        StringAssert.Contains(page.Markdown, "Wed 28 Oct 09:45");
        Assert.AreEqual(WebFetchStatus.Ok, page.Status);
    }

    [TestMethod]
    public async Task Convert_DropsHiddenCookieBanner()
    {
        var page = await HtmlPageConverter.ConvertAsync(ArticleWrappedPage, 200, CancellationToken.None);

        Assert.IsFalse(page.Markdown.Contains("We use cookies"), page.Markdown);
    }

    [TestMethod]
    public async Task Classify_OneTrustShell_IsConsentWall()
    {
        var page = await HtmlPageConverter.ConvertAsync(OneTrustShell, 200, CancellationToken.None);

        Assert.AreEqual(WebFetchStatus.BlockedByConsentWall, page.Status);
        Assert.IsFalse(page.Markdown.Contains("partners use cookies"), "consent text should not reach the output");
    }

    [TestMethod]
    public async Task Classify_EmptySpaRoot_IsJsRequired()
    {
        var page = await HtmlPageConverter.ConvertAsync(NextJsShell, 200, CancellationToken.None);

        Assert.AreEqual(WebFetchStatus.JsRequired, page.Status);
    }

    [TestMethod]
    public async Task Classify_LongArticleWithCookieBanner_IsOk()
    {
        var page = await HtmlPageConverter.ConvertAsync(LongArticleWithBanner, 200, CancellationToken.None);

        Assert.AreEqual(WebFetchStatus.Ok, page.Status);
        StringAssert.Contains(page.Markdown, "retry policy");
    }

    [TestMethod]
    public async Task Classify_NearEmptyBody_IsEmpty()
    {
        var page = await HtmlPageConverter.ConvertAsync(EmptyPage, 200, CancellationToken.None);

        Assert.AreEqual(WebFetchStatus.Empty, page.Status);
    }

    [TestMethod]
    public void ReadableLength_CountsLinkTextButNotUrls()
    {
        var length = PageShellDetector.ReadableLength("[Room 13](/schedule/sessions/1190251) ![logo](/logo.png)");

        Assert.AreEqual("Room 13".Length, length);
    }
}
