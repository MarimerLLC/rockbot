---
title: Knowledge graph
nav_order: 12
---

# Knowledge graph

RockBot includes a knowledge graph subsystem that stores entities and their relationships
as subject-predicate-object triples. This enables the agent to reason about connections
between people, projects, tools, and topics mentioned in conversations, and to surface
relevant relationship context automatically during inference.

---

## Data model

### Entities

A `KnowledgeEntity` represents a named thing in the agent's world:

```csharp
public sealed record KnowledgeEntity(
    string Id,                                          // Unique identifier
    string Name,                                        // Primary display name
    KnowledgeEntityType EntityType,                     // Person, Project, Topic, Tool, Event, Document
    IReadOnlyList<string> Aliases,                      // Alternative names/spellings
    IReadOnlyDictionary<string, string>? Metadata,      // Arbitrary key-value data
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastReferencedAt = null              // Updated by context builder on match
);
```

**Entity types:**

| Type | Description |
|---|---|
| `Person` | Contacts, collaborators, stakeholders |
| `Project` | Ongoing work, codebases, initiatives |
| `Topic` | Areas of interest, expertise, discussion themes |
| `Tool` | MCP services, integrations, platforms |
| `Event` | Meetings, deadlines, milestones |
| `Document` | Files, emails, artifacts |

### Triples

A `KnowledgeTriple` represents a directed relationship between two entities:

```csharp
public sealed record KnowledgeTriple(
    string Id,                  // Unique identifier
    string Subject,             // Entity ID or name (source)
    string Predicate,           // Relationship type (e.g. "works_on", "knows", "uses")
    string Object,              // Entity ID, name, or literal value (target)
    float Confidence,           // 0.0 to 1.0
    string? SourceEpisodeId,    // Optional link to the episodic memory that produced this
    DateTimeOffset CreatedAt
);
```

---

## Storage

`FileKnowledgeGraphStore` is the file-backed implementation of `IKnowledgeGraph`. It keeps
all entities and triples in memory and persists each as an individual JSON file on disk.
Thread safety is provided by a `SemaphoreSlim`.

```
{agentDataPath}/knowledge-graph/
  entities/
    {id}.json
  triples/
    {id}.json
```

The in-memory index is lazy-loaded on first access by scanning all JSON files in the
`entities/` and `triples/` directories. Malformed files are logged and skipped.

---

## Entity matching

When the agent receives a user message, `AgentContextBuilder` calls
`IKnowledgeGraph.FindEntitiesByNameAsync` to find entities whose name or aliases appear in
the message text.

**Matching rules:**

- Case-insensitive whole-word/phrase matching (prevents "Alice" matching inside "Malice")
- Names and aliases shorter than 3 characters are skipped to avoid noise from generic
  abbreviations ("AI", "PR", "it")
- Multi-word entity names match as complete phrases ("Azure DevOps" matches in
  "deploy via Azure DevOps pipeline")

---

## Context injection

When entities match the user's message, the context builder performs a two-phase expansion:

1. **Match** — `FindEntitiesByNameAsync(currentUserContent)` returns matching entities
2. **Traverse** — BFS traversal from matched entities up to `MaxHops` (default 2),
   collecting all triples discovered along the way

The resulting triples are injected as a system message (capped at `MaxExpandedTriples`,
default 15):

```
Related knowledge graph connections:
- Alice --works_on--> RockBot (confidence=0.90)
- RockBot --uses--> RabbitMQ (confidence=0.85)
- Alice --knows--> Bob (confidence=0.75)
```

**Staleness tracking:** Matched entity IDs are passed to `TouchEntitiesAsync`, which
updates `LastReferencedAt` to the current time. This lets the graph consolidation dream pass
identify entities that are never referenced and may be candidates for cleanup.

---

## Graph traversal

`TraverseAsync` performs breadth-first search from one or more seed entity IDs:

```
Seed: [Alice]     MaxHops: 2

Hop 1: Alice --works_on--> RockBot         (RockBot added to frontier)
Hop 2: RockBot --uses--> RabbitMQ          (RabbitMQ added to frontier)
        Bob --works_on--> RockBot           (Bob already discovered via object match)
```

- Cycles are handled — visited nodes are tracked and not re-expanded
- Both subject and object matches are followed (bidirectional traversal)
- Triples are deduplicated by ID and returned in discovery order

---

## Dream cycle integration

The dream service runs two knowledge-graph-specific passes during each cycle (both require
`IKnowledgeGraph` to be registered):

### Entity extraction pass

**Input:** Recent conversation log entries grouped by session, plus existing entity catalog

**What the LLM does:**
- Identifies new entities (people, projects, tools, events, documents) from conversations
- Creates triples describing relationships between entities
- References existing entity IDs when possible to avoid duplicates
- Assigns entity types, aliases, and confidence scores

**Directive file:** `entity-dream.md` (relative to agent data path). A built-in fallback is
used when the file does not exist.

**Controlled by:** `DreamOptions.EntityExtractionEnabled` (default `true`)

### Graph consolidation pass

Runs **after** entity extraction so newly created entities are included.

**Input:** All entities and triples in the graph, with creation dates and last-referenced
timestamps

**What the LLM does:**
- Deletes stale entities that have never been referenced and are old
- Removes redundant or low-confidence triples
- Cleans up entities with no remaining connections

**Directive file:** `graph-consolidation-dream.md` (relative to agent data path). A built-in
fallback is used when the file does not exist.

**Controlled by:** `DreamOptions.GraphConsolidationEnabled` (default `true`)

---

## Configuration

```csharp
public sealed class KnowledgeGraphOptions
{
    public string BasePath { get; set; } = "knowledge-graph";   // Relative to agent data path
    public int MaxHops { get; set; } = 2;                       // BFS depth for context expansion
    public int MaxExpandedTriples { get; set; } = 15;           // Max triples injected per turn
}
```

---

## DI registration

```csharp
builder
    .WithKnowledgeGraph()                               // Defaults
    .WithKnowledgeGraph(o =>                             // Custom options
    {
        o.MaxHops = 3;
        o.MaxExpandedTriples = 20;
    });
```

The knowledge graph is optional — when not registered, `AgentContextBuilder` skips entity
matching and traversal, and the dream service skips both extraction and consolidation passes.

---

## Relationship to other memory tiers

The knowledge graph is a **structural overlay** on top of the long-term memory system. Where
long-term memory stores facts as flat text entries retrieved by keyword search, the knowledge
graph captures typed relationships between entities and enables graph traversal to surface
connections that keyword search alone would miss.

Entity extraction draws from episodic memories and conversation logs — the same sources that
feed long-term memory consolidation — but produces structured entity/triple data rather than
free-text entries.

See [memory.md](memory.md) for the full memory architecture and
[dream-service.md](dream-service.md) for the complete dream cycle pass sequence.
