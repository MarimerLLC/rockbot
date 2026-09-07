using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Provides the agent with a usage guide for the shared-volume file tools.
/// </summary>
/// <remarks>
/// The <c>analyze_file</c> section is conditional on the same predicate the registrar uses, so
/// the guide never documents a tool this deployment does not offer — and never omits one it does,
/// whichever of the two hosted services starts first.
/// </remarks>
internal sealed class FileSystemToolSkillProvider(IServiceProvider services) : IToolSkillProvider
{
    private bool HasVision => VisionTiers.From(services.GetService<LlmTierOptions>()).Length > 0
                              && services.GetService<ILlmClient>() is not null;

    public string Name => "files";

    public string Summary => HasVision
        ? "Shared-volume file tools (file_write, file_edit, file_read, file_list, file_delete, file_get_path, analyze_file)."
        : "Shared-volume file tools (file_write, file_edit, file_read, file_list, file_delete, file_get_path).";

    public string GetDocument() => HasVision
        ? BaseDocument + AnalyzeFileSection
        : BaseDocument;

    private const string BaseDocument =
        """
        # Shared Volume File Tools Guide

        These tools provide direct access to files on the shared volume — a persistent
        filesystem shared across the agent, script pods, and other RockBot services.


        ## When to Use These Tools

        Use these tools when you need to:
        - Create or update files that persist beyond a single conversation turn
        - Read files produced by scripts or other services
        - Provide a local file path to other tools (e.g. OneDrive upload)
        - List or clean up files on the shared volume


        ## Changing an Existing File: Edit, Don't Rewrite

        `file_write` replaces the **entire** file. Any content you do not reproduce in
        full is gone — and on a long document you will not reliably reproduce it in full.

        So when a file already exists and you are changing part of it, use `file_edit`.
        Reserve `file_write` for creating new files or deliberately replacing a whole
        short one.

        This matters most for durable content — reference documents, notes, and records
        that accumulate over time. Losing a paragraph from those is often invisible until
        long after the edit.


        ## Tool Reference

        ### file_edit
        Replace an exact piece of text in an existing file, leaving everything else
        byte-for-byte untouched.

        ```
        file_edit(
          path: "canon/NPCs.md",
          old_string: "**Georgie** — dock foreman, neutral",
          new_string: "**Georgie** — dock foreman, owes the crew a favour"
        )
        ```

        Rules:
        - `old_string` must match the file **exactly**, including whitespace and
          indentation. Read the file first and copy the text verbatim rather than
          reconstructing it from memory.
        - `old_string` must match **exactly once**. If it appears more than once the edit
          is refused — include more surrounding text to make the match unique, or pass
          `replace_all: true` to change every occurrence.
        - Use an empty `new_string` to delete the matched text.
        - The file must already exist; use `file_write` to create it.

        A refused edit is information, not an obstacle. "Not found" means your `old_string`
        does not match the file — re-read it rather than retrying the same text. "Occurs N
        times" means you must disambiguate; do not switch to `file_write` to work around it.

        ### file_write
        Write UTF-8 text to a file on the shared volume. Parent directories are created
        automatically. Replaces the whole file — see the section above before using it on
        a file that already exists.

        ```
        file_write(path: "drafts/report.md", content: "# Weekly Report\n...")
        ```

        ### file_read
        Read the UTF-8 text content of a file.

        ```
        file_read(path: "drafts/report.md")
        ```

        ### file_list
        List all files as a JSON array of relative paths. Use the optional `prefix`
        parameter to filter by directory.

        ```
        file_list()                       // all files
        file_list(prefix: "drafts/")      // only files under drafts/
        ```

        ### file_delete
        Delete a single file from the shared volume.

        ```
        file_delete(path: "tmp/scratch.txt")
        ```

        ### file_get_path
        Returns the absolute local filesystem path for a file. Use this when another
        tool requires a local path rather than content (e.g. uploading to OneDrive).

        ```
        file_get_path(path: "exports/chart.png")
        // → "/rockbot/shared/exports/chart.png"
        ```


        ## Path Conventions

        Files are organized by purpose:
        - `tmp/` — temporary files, cleaned up after 1 day
        - `drafts/` — work-in-progress files, cleaned up after 14 days
        - `exports/` — final deliverables, cleaned up after 14 days
        - `scripts/` — output from script executions


        ## Everything Expires by Default

        **Every file on the shared volume is deleted 30 days after it was last modified,
        whatever directory it is in.** A path you invent yourself is not durable storage:
        `canon/notes.md` will be swept 30 days after your last edit to it, exactly when it
        has settled into being worth keeping.

        The sweep keys on modification time, so the files most at risk are the ones you
        read often and edit rarely — which is what long-lived reference content looks like.

        Only paths an operator has added to the deployment's `shared.protectedPaths`
        setting are exempt. You cannot set that yourself. So:

        - For content that must outlive 30 days without an edit, ask the user to have the
          prefix protected, and say plainly that it will be deleted otherwise.
        - Do not assume a directory is protected because it sounds permanent, or because
          content you wrote there is still present today.
        - Prefer memory tools over files for things you need to remember indefinitely.


        ## Working with Scripts

        Scripts run in ephemeral containers with the shared volume mounted at the path
        in the `ROCKBOT_SHARED_PATH` environment variable. Scripts write files directly
        to that path — no HTTP or API calls needed.

        **Typical workflow:**
        1. Write a script that saves output to `os.environ['ROCKBOT_SHARED_PATH']`
        2. Have the script print the relative path to stdout on success
        3. After the script completes, use `file_read` or `file_get_path` to access
           the output


        ## Common Pitfalls

        - All paths are relative to the shared volume root — do not use absolute paths
        - Path traversal outside the shared volume is blocked for security
        - Files in `tmp/` are automatically cleaned up daily — use `drafts/` or
          `exports/` for files that need to persist longer
        - `file_read` and `file_write` handle UTF-8 text only. To create a binary file, use
          a script; to pass one to another tool, use `file_get_path`
        """;

    /// <summary>
    /// Appended only when a configured tier accepts image input. Leads with the failure it
    /// replaces: reading an image with <c>file_read</c> is the mistake this tool exists to
    /// prevent, and it is expensive — a single image can fill working memory with chunked
    /// mojibake before the agent notices anything is wrong.
    /// </summary>
    private const string AnalyzeFileSection =
        """


        ## Looking at Images: analyze_file

        `file_read` cannot read an image. It will return thousands of characters of unusable
        text, chunk them into working memory, and leave you no closer to knowing what the image
        shows. Use `analyze_file` instead — for diagrams, screenshots, charts, scans, photos,
        and anything else you cannot read as text.

        ```
        analyze_file(
          path: "attachments/architecture.png",
          prompt: "Describe the components and how they connect.",
          tier: "high"
        )
        // → "Three services arranged left to right. The gateway on the left ..."
        ```

        The file is shown to a vision-capable model as an actual image. What comes back is that
        model's answer to your prompt — not the file's bytes, which never enter your context.

        Because of that, **the answer is all you get**. The model that looked at the image is
        not in this conversation and cannot be asked a follow-up; a second question means a
        second call and a second look. So ask for everything you need in one prompt: "list every
        label and the arrows between them" rather than "what is this diagram of". Specific
        prompts also produce specific answers — an open-ended prompt gets an open-ended summary
        that often omits the one detail you needed.

        The `tier` parameter is optional and defaults to balanced. Use `high` for dense diagrams,
        small text, or fine visual detail.

        Not every deployment can do this. When this section is absent from the guide, no
        configured model accepts images and there is no way to look at one — say so plainly
        rather than reading the file as text and guessing at what it contains.
        """;
}
