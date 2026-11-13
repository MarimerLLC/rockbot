using RockBot.Tools;

namespace RockBot.Agent;

/// <summary>
/// Guide for producing a slide deck or other visual document the user can actually open, and for
/// checking it before saying it is done (#669). Written after the agent "shared" a deck by
/// uploading Markdown source that used none of its framework's features.
/// </summary>
internal sealed class PresentationsSkillProvider : IToolSkillProvider
{
    public string Name => "presentations";

    public string Summary =>
        "Building a slide deck or visual HTML document the user can open: format choice, " +
        "animation, checking your own output (render_screenshot, analyze_file), and delivery (attach_file).";

    public string GetDocument() =>
        """
        # Presentations and Visual Documents Guide

        Read this before you build a slide deck, an animated presentation, or any HTML
        page the user is meant to look at rather than read as text.

        The rule that matters most: **the user must end up with something they can open
        and see as slides.** A Markdown file is source, not a deck. Uploading or attaching
        source and calling it the deck is a failure, however good the content is.


        ## Step 1 — Choose a Format You Can Finish

        You can write files and run Python. You cannot run a Node build. So:

        - **Default: one self-contained HTML file using reveal.js from a CDN.** It needs no
          build step, it animates, and it opens in any browser.
        - **Do not choose Slidev, Marp, MDX decks or anything else that needs
          `npm`/`npx` to produce slides.** You can write their Markdown but nothing here
          can turn it into slides.
        - If the user names a framework that needs a build, say plainly that you can write
          the source but cannot build it, and offer the reveal.js version they can open now.
        - If the user wants PowerPoint (`.pptx`), that is a different job: generate it with
          a script (`python-pptx`) and deliver the file. This guide covers HTML decks.

        "Self-contained" means **one `.html` file**. Styles and scripts of your own go
        inline. Libraries, fonts and images load from public `https://` URLs. A second
        local file (`style.css`, `img/chart.png`) will not load for the user.


        ## Step 2 — Write a Deck That Uses the Framework

        Bullets separated by slide breaks are a text file with extra steps. Use what
        reveal.js gives you:

        - **Fragments** reveal points one at a time: `class="fragment"`, with variants
          such as `fade-up`, `fade-in-then-semi-out`, `highlight-red`.
        - **Transitions** between slides: `data-transition="slide"`, `fade`, `convex`, `zoom`.
        - **Auto-animate** moves matching elements between two slides: put
          `data-auto-animate` on both `<section>`s.
        - **Vertical slides**: nest `<section>` inside `<section>` for a drill-down.
        - **Layout**: two columns with CSS grid or flex; `class="r-fit-text"` for a big
          statement; `class="r-stretch"` for an image that fills the slide.
        - **Speaker notes**: `<aside class="notes">…</aside>`.
        - **Backgrounds**: `data-background-color`, `data-background-gradient`.

        Keep each slide to one idea and roughly six lines of text at most. Text that
        overflows the slide is the most common defect; Step 3 is how you catch it.

        A skeleton to start from:

        ```html
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Deck title</title>
          <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/reveal.js@5/dist/reveal.css">
          <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/reveal.js@5/dist/theme/black.css">
          <style>
            .cols { display: grid; grid-template-columns: 1fr 1fr; gap: 2rem; align-items: center; }
          </style>
        </head>
        <body>
          <div class="reveal"><div class="slides">

            <section data-transition="fade">
              <h1 class="r-fit-text">Deck title</h1>
              <p>Subtitle · date</p>
            </section>

            <section data-auto-animate>
              <h2>One idea</h2>
              <ul>
                <li class="fragment fade-up">First point</li>
                <li class="fragment fade-up">Second point</li>
              </ul>
              <aside class="notes">What to say here.</aside>
            </section>

            <section data-auto-animate>
              <h2>One idea</h2>
              <div class="cols">
                <div>Left column</div>
                <div class="fragment">Right column</div>
              </div>
            </section>

          </div></div>
          <script src="https://cdn.jsdelivr.net/npm/reveal.js@5/dist/reveal.js"></script>
          <script>
            Reveal.initialize({ transition: 'slide', slideNumber: true });
          </script>
        </body>
        </html>
        ```

        Other themes in the same folder: `white`, `league`, `beige`, `night`, `serif`,
        `simple`, `solarized`, `moon`, `dracula`, `sky`, `blood`.

        The deck is shown in a sandbox with no storage and no access to the app around
        it. Do not rely on `localStorage`, cookies, or the page URL (leave `hash` and
        `history` off in `Reveal.initialize`).

        Write the file under `exports/` (or `drafts/` while iterating):

        ```
        file_write(path: "exports/q3-review.html", content: "<!doctype html>…")
        ```


        ## Step 3 — Look at It Before You Say It Is Done

        You have not seen your deck until you render it.

        ```
        render_screenshot(path: "exports/q3-review.html")
        → Saved a screenshot … to tmp/screenshots/q3-review-20261113-101500.png
        analyze_file(path: "tmp/screenshots/q3-review-20261113-101500.png",
                     prompt: "This is the first slide of a reveal.js deck. Is it styled as a slide
                              (not unstyled HTML)? Does any text overflow or get cut off? Is the
                              text legible against the background?")
        ```

        - The screenshot is the **first viewport**, so the title slide. Unstyled text on
          a white page means the CDN links are wrong or the HTML is malformed: fix that
          first, since every slide is affected.
        - To check a specific dense slide, write a temporary copy under `tmp/` with that
          slide moved first, screenshot the copy, and delete it. Do not reorder the real
          deck.
        - Fix what you find with `file_edit`, then screenshot again. One or two rounds is
          normal. Do not loop more than three times; report what is still imperfect.
        - If `render_screenshot` or `analyze_file` is not in your tool list, say that you
          could not check the result visually. Do not claim it was verified.

        When you report, say what you saw: "I rendered the title slide and it shows …".


        ## Step 4 — Deliver Something the User Can Open

        ```
        attach_file(path: "exports/q3-review.html", fileName: "Q3 review deck")
        ```

        The result says what this user's client does with it:

        - **It can preview HTML:** the deck appears live in your reply with an "open in
          new tab" link. Tell the user to click into it and use the arrow keys. Do not
          paste the HTML or a file path as well.
        - **It cannot:** the user sees only a placeholder line. Upload the `.html` file to
          the user's cloud storage (OneDrive, SharePoint) with the MCP tools and give them
          the link, and say they need to download it and open it in a browser, since cloud
          viewers show HTML as text.

        The attached copy is a snapshot. After the user asks for changes, edit the file
        in `exports/` and call `attach_file` again in the reply that presents the new
        version.

        Attached files are kept for about 30 days. For anything the user wants to keep,
        also upload it to their storage.

        If `attach_file` is not in your tool list (a subagent or a scheduled task), return
        the shared-volume path in your result so the primary agent can attach it.


        ## Other Visual Documents

        The same loop works for a one-page report, a dashboard mock-up or a diagram page:
        one self-contained `.html` file, `render_screenshot`, `analyze_file`, `attach_file`.
        For a single chart or diagram, an image is simpler: have a script write a PNG and
        use `attach_image`.


        ## Common Pitfalls

        - Uploading or attaching Markdown source and calling it a deck
        - Picking a framework that needs a build step you cannot run
        - A deck with no fragments, transitions or layout: bullet text between slide breaks
        - Referencing a second local file; only the one `.html` file reaches the user
        - Saying "the deck is ready" without having rendered and looked at it
        - Reporting a visual check you did not run
        """;
}
