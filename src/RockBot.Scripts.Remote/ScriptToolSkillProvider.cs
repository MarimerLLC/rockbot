using RockBot.Tools;

namespace RockBot.Scripts.Remote;

/// <summary>
/// Provides the agent with a usage guide for the Python script execution tool.
/// Registered automatically when <c>AddRemoteScriptRunner()</c> is called.
/// </summary>
internal sealed class ScriptToolSkillProvider : IToolSkillProvider
{
    public string Name => "scripts";
    public string Summary => "Execute Python scripts in isolated containers (execute_python_script).";

    public string GetDocument() =>
        """
        # Python Script Execution Guide

        One tool runs arbitrary Python code in a secure, ephemeral container:
        `execute_python_script`. Use it for calculations, data processing, format
        conversions, API calls, and any task that benefits from real code execution
        rather than approximation.


        ## When to Use This Guide

        Consult this guide when a task requires precise computation, structured data
        manipulation, or logic that would be error-prone to reason through without running
        code. If you have a saved script skill for this type of task, load it first.


        ## Step 0 — Check for an Existing Script

        Before writing a new script, check whether a working script for this task already
        exists. The injected skill index shows a bracketed `[Python, ...]` tag after any
        skill that has saved resources — scan for tags containing `Python` on skills whose
        summary matches your task.

        Two places scripts may live:

        1. **As a `Python`-type resource on a task-relevant skill** — the preferred pattern.
           If a relevant skill shows `[Python]` (or `[Python, ...]`), call `get_skill` to see
           the manifest, then `get_skill_resource(<skill>, <filename>.py)` to fetch the script.
           Adapt it to the current inputs and run.
        2. **As a standalone `scripts/{task-type}` skill** — an older pattern, still valid.
           Skills named `scripts/csv-processing`, `scripts/date-calculations`, etc. Load with
           `get_skill` and use the script inside the markdown content.

        Either way: don't re-derive a script the system has already debugged.


        ## Step 1 — Write the Script

        The script must print its results to **stdout** — that is the only output channel
        returned to you. Anything written to stderr is captured separately and indicates
        an error.

        **Rules for writing scripts:**
        - Use `print()` for all output you want returned
        - Print structured data as JSON for easy parsing: `import json; print(json.dumps(result))`
        - Exit with code 0 on success; any non-zero exit code signals failure
        - Keep scripts focused — one clear task per invocation
        - Avoid interactive input (`input()` will hang and time out)

        **Parameters**
        - `script` (string, required) — Python source code to execute
        - `input_data` (string, optional) — arbitrary data passed as the `ROCKBOT_INPUT`
          environment variable; read it with `os.environ.get("ROCKBOT_INPUT")`
        - `pip_packages` (array of strings, optional) — packages installed before the
          script runs (e.g. `["requests", "pandas"]`); adds a few seconds of startup time
        - `timeout_seconds` (integer, optional, default 30) — maximum wall-clock runtime;
          increase for long-running data tasks, keep low for quick calculations


        ## Step 2 — Run the Script

        **Simple calculation example:**
        ```
        execute_python_script(
          script: "import math\nresult = math.sqrt(2) * 1000\nprint(f'{result:.6f}')"
        )
        ```

        **Processing input data:**
        ```
        execute_python_script(
          script: "import os, json\ndata = json.loads(os.environ['ROCKBOT_INPUT'])\nprint(sum(data))",
          input_data: "[1, 2, 3, 4, 5]"
        )
        ```

        **Using a third-party library:**
        ```
        execute_python_script(
          script: "import requests\nr = requests.get('https://api.example.com/data')\nprint(r.json())",
          pip_packages: ["requests"],
          timeout_seconds: 60
        )
        ```


        ## Step 3 — Interpret the Result

        The response contains:
        - `output` — everything the script printed to stdout (your result)
        - `stderr` — any error output or tracebacks
        - `exit_code` — 0 means success; non-zero means the script failed
        - `elapsed_ms` — how long the script took

        If `exit_code` is non-zero:
        1. Read the `stderr` for the traceback or error message
        2. Fix the script — common issues: syntax errors, missing imports, wrong variable names
        3. Re-run with the corrected script
        4. If the error is a timeout, either optimise the script or increase `timeout_seconds`


        ## Step 4 — Report Results

        - Parse and summarise the script output rather than returning it verbatim
        - For large outputs, extract the key values and mention that full output is available
        - If the script failed after multiple attempts, explain what was tried and why it failed


        ## Step 5 — Save Working Scripts for Reuse

        Once a script has been written, debugged, and confirmed to produce the right output,
        save it so future sessions can skip the writing and debugging steps.

        **Preferred: save as a `Python` resource on a task-relevant skill.** If there's an
        existing skill covering the task the script supports (or if the task itself deserves
        a new skill), attach the script as a resource via `save_skill` with a `resources`
        list entry of type `Python`. This keeps the script next to the narrative that explains
        *when* and *why* to use it, and the skill index auto-surfaces a `[Python]` tag so
        future sessions find it without loading the skill first.

        Example resource entry:
        ```
        { "filename": "summary.py", "type": "Python",
          "description": "Summarise a CSV of daily sales into totals by category.",
          "content": "<script source>" }
        ```

        **Alternative: standalone `scripts/{task-type}` skill.** For general-purpose utility
        scripts that aren't tied to a specific task domain (timezone conversion, base64
        decoding, etc.), save as a skill of its own with markdown explaining usage and the
        script in a fenced code block.

        Either way, Step 0 on the next invocation will surface it via the skill index
        `[Python]` tag and the agent can run the proven script with adapted inputs.


        ## Best Practices

        - **Print JSON for structured output** — it's easy to parse and avoids ambiguity
        - **Keep timeout realistic** — 30s covers most tasks; set higher only when needed
          (pip installs, network calls, large data); never set it unnecessarily high
        - **Use `input_data` for variable inputs** — pass the dynamic part as input data
          rather than embedding it in the script; makes the script reusable
        - **Test logic in small steps** — if a script is complex, break it into sequential
          invocations and verify intermediate results before proceeding
        - **Avoid side effects unless intended** — scripts can make network calls and write
          to stdout; be deliberate about what the script does


        ## Reading and Writing Files (shared volume)

        **CRITICAL:** Script containers do NOT share the agent's working directory.
        The ONLY persistent filesystem is the shared volume, mounted at the path in
        the `ROCKBOT_SHARED_PATH` environment variable (typically `/rockbot/shared`).

        **You MUST use absolute paths built from `ROCKBOT_SHARED_PATH` for ALL file
        access.** Relative paths like `staging/file.json` will fail — the container's
        working directory is NOT the shared volume.

        **Reading files** (e.g. files created by wisps via `output_to`):
        ```python
        import os, json
        shared = os.environ['ROCKBOT_SHARED_PATH']
        with open(os.path.join(shared, 'staging', 'events.json')) as f:
            data = json.load(f)
        ```

        **Writing files:**
        ```python
        import os
        shared = os.environ['ROCKBOT_SHARED_PATH']
        path = os.path.join(shared, 'exports', 'report.xlsx')
        os.makedirs(os.path.dirname(path), exist_ok=True)
        # ... write the file ...
        print(f"exports/report.xlsx")
        ```

        After the script completes, use `file_read` or `file_get_path` to access the output.

        ### Generating files for MCP attachments

        The `attachments/` subdirectory under `ROCKBOT_SHARED_PATH` is where files destined
        for MCP tools live (e.g. attachments on a `send_email` call). A script writes the
        file there and returns the path; the agent then hands the path to the MCP tool and
        the bridge takes care of upload/download — no base64 ever crosses the LLM context.

        ```python
        import os, json
        shared = os.environ['ROCKBOT_SHARED_PATH']
        out = os.path.join(shared, 'attachments', 'q3-report.xlsx')
        os.makedirs(os.path.dirname(out), exist_ok=True)
        # ... build the workbook ...
        print(json.dumps({"path": out, "name": "q3-report.xlsx"}))
        ```

        The agent then calls something like:
        `mcp_invoke_tool(server_name="calendar-mcp", tool_name="send_email",
        arguments={ "to": "...", "attachments": [{ "path": out }] })`.


        ## Common Pitfalls

        - Forgetting that only stdout is returned — tracebacks go to stderr and won't appear
          in `output`, only in the error details
        - Not handling the case where `ROCKBOT_INPUT` is absent when `input_data` is optional
        - Hitting the 30s timeout with pip installs for heavy packages — increase
          `timeout_seconds` when installing large dependencies like `torch` or `scipy`
        - Printing debug statements that pollute the output — use stderr for debug output:
          `import sys; print("debug", file=sys.stderr)`
        - **Using relative file paths** — the container's cwd is NOT `/rockbot/shared`.
          Always build paths with `os.path.join(os.environ['ROCKBOT_SHARED_PATH'], ...)`.
          If wisps wrote files via `output_to: "staging/file.json"`, the script must read
          them at `os.path.join(shared, 'staging', 'file.json')`, not `"staging/file.json"`
        - Writing files outside `ROCKBOT_SHARED_PATH` — only files on the shared volume
          persist after the container exits
        """;
}
