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


        ## Step 0 — Check for an Existing Script Skill

        Before writing a new script, check whether a reusable script for this task type
        already exists:

        ```
        list_skills()
        ```

        Look for skills named `scripts/{task-type}` (e.g. `scripts/csv-processing`,
        `scripts/date-calculations`, `scripts/image-resize`). If one exists, load it
        with `get_skill`, adapt the script to the current inputs, and run it.


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


        ## Step 5 — Save Reusable Scripts as Skills

        When you write a script that solves a general problem worth reusing, save it as
        a skill so future tasks can skip the writing step.

        Call `save_skill` with:
        - **name**: `scripts/{task-type}` in lowercase with hyphens
          (e.g. `scripts/csv-summary`, `scripts/timezone-conversion`, `scripts/base64-decode`)
        - **content**: a markdown document containing:
          - What the script does and when to use it
          - The script itself in a fenced code block
          - How to pass input data and what format it expects
          - Expected output format and how to interpret it
          - Any pip packages required and why

        Future tasks load the skill in Step 0 and run the proven script with adapted inputs.


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


        ## Common Pitfalls

        - Forgetting that only stdout is returned — tracebacks go to stderr and won't appear
          in `output`, only in the error details
        - Not handling the case where `ROCKBOT_INPUT` is absent when `input_data` is optional
        - Hitting the 30s timeout with pip installs for heavy packages — increase
          `timeout_seconds` when installing large dependencies like `torch` or `scipy`
        - Printing debug statements that pollute the output — use stderr for debug output:
          `import sys; print("debug", file=sys.stderr)`
        - Writing scripts that assume a filesystem persists between runs — each execution
          is a fresh ephemeral container with no shared state
        """;
}
