using RockBot.Tools;

namespace RockBot.Skills;

/// <summary>
/// Provides the agent with a usage guide for the skill and rules tools.
/// Registered automatically when <c>WithSkills()</c> is called.
/// </summary>
public sealed class SkillToolSkillProvider : IToolSkillProvider
{
    public string Name => "skills";
    public string Summary => "Skill documents (reusable procedures) and behavioral rules — how to create, use, maintain them, and attach structured artifacts as sub-resources.";

    public string GetDocument() =>
        """
        # Skills and Rules Guide

        Two systems let the agent build up institutional knowledge and enforce
        consistent behavior over time.

        | System | Purpose | Scope |
        |---|---|---|
        | Skills | Reusable step-by-step procedures for recurring task types | Loaded on demand |
        | Rules | Hard behavioral constraints enforced on every single turn | Always active |


        ## Skills

        A skill is a named markdown document that describes how to complete a specific
        type of task. Skills are the agent's long-term procedural memory — the equivalent
        of "I've done this before; here's how."

        The skill index is shown at the start of each session. When a skill is relevant
        to the user's request, load it with `get_skill` and follow its instructions.


        ### When to create a skill

        - You complete a multi-step task successfully and would repeat the same approach
          for similar requests in future
        - You discover a reliable workflow for using a tool service (e.g. `mcp/ms365`,
          `web/dotnet-docs`, `scripts/csv-processing`)
        - The user asks you to remember how to do something
        - A task type has enough nuance or steps that rediscovering the process from
          scratch would waste time

        ### When NOT to create a skill

        - One-off tasks specific to this conversation with no reuse value
        - Tasks already fully covered by an existing skill (update the existing one instead)
        - Simple single-step actions that need no procedure


        ### Skill naming conventions

        Skills use slash-separated hierarchical names. Established prefixes:

        | Prefix | Use for |
        |---|---|
        | `mcp/{server-name}` | How to use a specific MCP server |
        | `web/{topic}` | Reliable sources and search patterns for a topic |
        | `scripts/{task-type}` | Reusable Python scripts for a task category |
        | (no prefix) | General procedures (e.g. `plan-meeting`, `write-report`) |

        Use lowercase with hyphens. Forward slashes create subcategories in the index.


        ### list_skills

        Returns the full skill index with one-line summaries. The index is also injected
        at the start of each session — use this tool mid-session to refresh it.

        ```
        list_skills()
        ```


        ### get_skill

        Loads the full content of a named skill. Call this when the index shows a skill
        relevant to the current task — always load and follow it rather than improvising.
        If the skill has sub-resources (scripts, schemas, etc.), they are listed at the
        end of the response; use `get_skill_resource` to fetch them on demand.

        **Parameters**
        - `name` (string, required) — the skill name as shown in the index

        ```
        get_skill("mcp/ms365")
        ```


        ### get_skill_resource

        Fetches a single sub-resource file from a skill's resource folder. Call this
        when `get_skill` shows a resource that you need — e.g. a Python script or JSON
        schema that is referenced in the skill's instructions.

        **Parameters**
        - `skillName` (string, required) — the skill name
        - `filename` (string, required) — the filename shown in the manifest (e.g. `script.py`)

        ```
        get_skill_resource("scripts/csv-processing", "transform.py")
        ```


        ### save_skill

        Creates a new skill or updates an existing one. A one-line summary is generated
        automatically and added to the index. Pass structured artifacts (scripts, schemas,
        etc.) as `resources` rather than embedding them in the markdown body — this keeps
        the markdown readable and lets native tooling (linters, diff tools) see the files.

        **Parameters**
        - `name` (string, required) — skill name following the naming conventions above
        - `content` (string, required) — full skill content in markdown
        - `resources` (array, optional) — sub-resource files to save alongside the skill.
          Each entry: `{ filename, type, description, content }`.
          Providing this list **replaces** all previously saved resources for this skill.
          Omit or pass an empty array to keep the skill markdown-only.

        Available resource types: `Python`, `Wisp`, `JsonSchema`, `Markdown`, `Text`, `Other`.

        ```
        save_skill(
          name: "scripts/csv-processing",
          content: "# CSV Processing\n\n## When to use\n...",
          resources: [
            {
              filename: "transform.py",
              type: "Python",
              description: "Transforms raw CSV rows into the normalized output format",
              content: "import csv\n..."
            }
          ]
        )
        ```

        **Writing a good skill document:**
        - Start with a `# Title` heading
        - Include a "When to use" section so the agent knows when to load the skill
        - Number the steps — skills are procedures, not reference docs
        - Include concrete examples with actual parameter values
        - Note any pitfalls or edge cases discovered during real use
        - Keep it focused on one task type; create separate skills for related but distinct tasks
        - Move scripts, schemas, and other structured artifacts into `resources` instead of embedding them in markdown

        **Updating an existing skill:** use `edit_skill`, not `save_skill` — see below.
        Reserve `save_skill` for creating a skill, replacing a short one outright, or changing
        the `resources` bundle.


        ### edit_skill

        Replaces an exact piece of text in an existing skill's markdown, leaving the rest of
        the document byte-for-byte untouched. This is the normal way to improve a skill:
        adding the pitfall you just hit, correcting a step, updating an example.

        **Parameters**
        - `name` (string, required) — the skill to edit
        - `old_string` (string, required) — exact text to find in the skill's markdown
        - `new_string` (string, required) — replacement text; empty string deletes the match
        - `replace_all` (boolean, optional, default false) — change every occurrence

        ```
        edit_skill(
          name: "plan-meeting",
          old_string: "3. Send the invite.",
          new_string: "3. Send the invite.\n4. Check the room booking — it is not automatic."
        )
        ```

        Why this rather than `save_skill`:
        - `save_skill` replaces the **entire** body. Anything you do not reproduce verbatim is
          gone, and on a long procedure you will not reproduce it verbatim.
        - `save_skill` also clears the summary and regenerates it with a background LLM call,
          so the skill index reads "(summary pending)" until it returns. An edit keeps it.

        Rules:
        - Call `get_skill` first and copy `old_string` from what it returns.
        - `old_string` must match **exactly once**, or the edit is refused — add surrounding
          text or pass `replace_all: true`. Do not switch to `save_skill` to work around a
          refusal.


        ### delete_skill

        Deletes a skill by name. Use when a skill is obsolete, superseded by a better
        version under a different name, or no longer applicable.

        **Parameters**
        - `name` (string, required) — the skill name to delete

        ```
        delete_skill("old-workflow")
        ```


        ---

        ## Rules

        Rules are hard behavioral constraints injected into every system prompt with the
        same authority as the agent's core directives. Unlike skills (which are loaded
        on demand), rules are always active.

        ### When to add a rule

        - The user wants to permanently change how you respond
          ("always respond in French", "never use bullet points")
        - A constraint should apply regardless of context or conversation history
        - The user has corrected a habitual behavior they want consistently changed

        ### When NOT to add a rule

        - The user wants a preference for just this session (honor it conversationally)
        - The constraint is task-specific and shouldn't apply globally
        - It duplicates something already in the agent's directives


        ### add_rule

        Adds a permanent behavioral rule that persists across sessions.

        **Parameters**
        - `rule` (string, required) — clear behavioral constraint in plain language

        ```
        add_rule("Always respond in British English spelling")
        ```

        **Tips**
        - State rules as positive constraints where possible: "always do X" rather than
          "don't do Y"
        - Be specific — vague rules like "be more concise" are hard to apply consistently
        - Confirm with the user before adding rules they haven't explicitly requested


        ### edit_rule

        Changes part of an existing rule in place — narrowing it, widening it, or correcting a
        detail — without restating the whole rule.

        **Parameters**
        - `old_string` (string, required) — exact text to find within an active rule
        - `new_string` (string, required) — replacement text; empty string deletes the match
        - `replace_all` (boolean, optional, default false) — change every occurrence across all rules

        ```
        edit_rule(
          old_string: "never use bullet points",
          new_string: "never use bullet points in email drafts"
        )
        ```

        Call `list_rules` first and copy `old_string` verbatim. If it appears in more than one
        rule the edit is refused — include more of the rule text, or pass `replace_all: true`.
        Prefer this to `remove_rule` + `add_rule`, which requires restating the rule in full and
        moves it to the end of the list.


        ### list_rules

        Lists all currently active rules.

        ```
        list_rules()
        ```


        ### remove_rule

        Removes an active rule. The `rule` argument must match the stored text exactly —
        call `list_rules` first to get the exact wording.

        **Parameters**
        - `rule` (string, required) — exact text of the rule to remove

        ```
        remove_rule("Always respond in British English spelling")
        ```


        ---

        ## Best Practices

        - **Check the skill index before starting a multi-step task** — if a relevant
          skill exists, load it immediately with `get_skill` rather than improvising
        - **Update skills after every real use** — add the pitfalls and examples you
          discover with `edit_skill`; the skill document should get better each time you use
          it, and an edit cannot drop the parts you were not thinking about
        - **Prefer updating over creating** — before saving a new skill, check whether
          an existing one covers the same ground and could be extended instead
        - **Use resources for structured artifacts** — move Python scripts, JSON schemas,
          wisp definitions, and similar files into `resources` rather than embedding them
          in the markdown body; this keeps the markdown readable and lets `get_skill_resource`
          load them on demand without bloating the context
        - **Save working wisps back to their skill** — once a wisp is authored, debugged,
          and confirmed working, save the final definition as a `Wisp`-type resource on the
          relevant skill with a description. Future sessions that load the skill will see
          the resource in the manifest and can reuse it via `get_skill_resource` + `spawn_wisps`
          instead of re-authoring from scratch
        - **Check for Wisp resources before authoring** — when a loaded skill's manifest
          lists a Wisp resource, fetch it first; start from the saved definition and adapt
          it rather than composing a new one
        - **Rules are permanent and broad** — confirm intent with the user before adding
          one; they affect every future interaction
        - **Use `list_rules` to audit** — periodically surfacing active rules helps
          catch outdated or conflicting constraints


        ## Common Pitfalls

        - Loading a skill and then ignoring it — if you called `get_skill`, follow
          the instructions in it
        - Creating skills that are too broad — one skill per distinct task type works
          better than a monolithic "how to do everything" document
        - Embedding scripts or schemas inline in markdown — use `resources` instead so
          native tooling can read them and agents can load them without paying for the
          whole skill body
        - Adding rules for session-specific preferences — use conversational acknowledgment
          for per-session requests, rules only for permanent changes
        - Forgetting to update a skill after discovering a new pitfall — the next use
          of that skill will repeat the same mistake
        """;
}
