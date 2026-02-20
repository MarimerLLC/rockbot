# Memory Rules

These rules apply to all memory operations — saving, enriching, and consolidating.

## Categories

Categories are **slash-separated hierarchical paths** that map directly to subdirectory structure on disk:

- Related memories are physically grouped and retrieved together by searching a parent prefix
- Searching `user-preferences` returns everything under it, including `user-preferences/family`, `user-preferences/work`, etc.
- Choose categories that reflect the *topic* of the fact, not its source
- Prefer deeper paths for specificity (`user-preferences/pets` rather than just `user-preferences`) when a fact clearly belongs to a narrower topic
- Invent subcategories whenever a topic warrants its own grouping

**Suggested categories:**

| Category | Use for |
|---|---|
| `user-preferences` | Personal details, tastes, and opinions |
| `user-preferences/identity` | Name, background, heritage |
| `user-preferences/family` | Spouse, children, relatives, siblings |
| `user-preferences/pets` | Pets and animals |
| `user-preferences/work` | Job, employer, role, projects |
| `user-preferences/hobbies` | Interests, activities, passions |
| `user-preferences/music` | Music tastes and concert preferences |
| `user-preferences/location` | Where the user lives or spends time |
| `user-preferences/lifestyle` | Living situation, travel, daily life |
| `user-preferences/attitudes` | Opinions, values, outlook on life |
| `project-context/<name>` | Decisions, goals, and context for a specific project |
| `agent-knowledge` | Things learned about how to work well with this user |

## Content style

- Write content as a natural sentence that includes **synonyms and related terms** so keyword search is robust
- Example: write "Rocky has a dog — a Sheltie (Shetland Sheepdog) named Milo" rather than "Rocky has a Sheltie named Milo", so searches for "dog", "pet", "sheltie", or "Milo" all match
- Be specific and factual; do not pad with filler

## Tags

- Lowercase single words or short hyphenated phrases
- Include synonyms and related terms
- Examples: `ice-fishing`, `rv-living`, `hard-rock`, `eden-prairie`, `fifth-wheel`

## Durable vs ephemeral

Only store facts that will still be true and useful in a future conversation:

- **Save**: stable facts, preferences, relationships, named entities, recurring patterns, decisions
- **Do not save**: current physical position ("sitting by the fireplace"), what someone is momentarily doing ("Teresa is on a phone call"), temporary real-time states ("RV in storage because it's cold"), passing observations with no lasting significance
