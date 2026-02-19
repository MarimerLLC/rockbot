using RockBot.Tools;

namespace RockBot.Skills;

/// <summary>
/// Provides the agent with a usage guide for the skill and rules tools.
/// Registered automatically when <c>WithSkills()</c> is called.
/// </summary>
public sealed class SkillToolSkillProvider : IToolSkillProvider
{
    public string Name => "skills";
    public string Summary => "Skill documents (reusable procedures) and behavioral rules — how to create, use, and maintain them.";

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

        **Parameters**
        - `name` (string, required) — the skill name as shown in the index

        ```
        get_skill("mcp/ms365")
        ```


        ### save_skill

        Creates a new skill or updates an existing one. A one-line summary is generated
        automatically and added to the index.

        **Parameters**
        - `name` (string, required) — skill name following the naming conventions above
        - `content` (string, required) — full skill content in markdown

        ```
        save_skill(
          name: "plan-meeting",
          content: "# Plan a Meeting\n\n## When to use\n..."
        )
        ```

        **Writing a good skill document:**
        - Start with a `# Title` heading
        - Include a "When to use" section so the agent knows when to load the skill
        - Number the steps — skills are procedures, not reference docs
        - Include concrete examples with actual parameter values
        - Note any pitfalls or edge cases discovered during real use
        - Keep it focused on one task type; create separate skills for related but distinct tasks

        **Updating an existing skill:**
        - Load the current skill with `get_skill` first
        - Add new steps, examples, or pitfall notes discovered during use
        - Save with the same name to overwrite


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
          discover; the skill document should get better each time you use it
        - **Prefer updating over creating** — before saving a new skill, check whether
          an existing one covers the same ground and could be extended instead
        - **Rules are permanent and broad** — confirm intent with the user before adding
          one; they affect every future interaction
        - **Use `list_rules` to audit** — periodically surfacing active rules helps
          catch outdated or conflicting constraints


        ## Common Pitfalls

        - Loading a skill and then ignoring it — if you called `get_skill`, follow
          the instructions in it
        - Creating skills that are too broad — one skill per distinct task type works
          better than a monolithic "how to do everything" document
        - Adding rules for session-specific preferences — use conversational acknowledgment
          for per-session requests, rules only for permanent changes
        - Forgetting to update a skill after discovering a new pitfall — the next use
          of that skill will repeat the same mistake
        """;
}
