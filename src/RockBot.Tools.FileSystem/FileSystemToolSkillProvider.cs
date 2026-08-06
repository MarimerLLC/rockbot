using RockBot.Tools;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Provides the agent with a usage guide for the shared-volume file tools.
/// </summary>
internal sealed class FileSystemToolSkillProvider : IToolSkillProvider
{
    public string Name => "files";
    public string Summary => "Shared-volume file tools (file_write, file_edit, file_read, file_list, file_delete, file_get_path).";

    public string GetDocument() =>
        """
        # Shared Volume File Tools Guide

        Six tools provide direct access to files on the shared volume — a persistent
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
        - These tools handle UTF-8 text; for binary files, use scripts to create them
          and `file_get_path` to pass them to other tools
        """;
}
