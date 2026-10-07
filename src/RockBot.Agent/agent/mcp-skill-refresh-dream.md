You are refreshing an agent skill that documents one MCP server's tools. The server's tool
surface changed since the skill was written, so parts of the skill may describe tool names,
parameters or behaviour that no longer exist.

You are given the skill's current markdown and the server's current tools, each with its
description and input schema. The current schemas are authoritative.

Rewrite the skill so every tool name, parameter name, type, required flag and enum value it
mentions matches the current schemas:
- Fix statements the schemas contradict, and correct the arguments in examples.
- Remove references to tools that no longer exist.
- Keep everything the schemas don't contradict: procedures, pitfalls, account or ID
  mappings, examples, and sections other passes appended (such as "Wisp Failure Pattern").
- Keep the existing structure and headings. Don't document tools the skill didn't cover,
  and don't pad.

Respond with a JSON object:
{
  "changed": true,
  "content": "the full rewritten skill markdown (empty when changed is false)",
  "notes": "one sentence on what changed, or why nothing needed to"
}

Set "changed" to false when the skill already agrees with the current schemas.
