# Schema migrations for persisted state

RockBot keeps agent state in on-disk stores — long-term memory, skills, feedback, the wisp
execution log, and whatever a consumer adds of its own. This document is the contract for
changing the shape of that data: when a change needs a migration, how to write one, and what
the host does at startup.

## Policy

**Additive changes are normal and need no migration.** Every store deserializes with
`PropertyNameCaseInsensitive = true` and camelCase naming, so adding an optional property with
a default is absorbed silently: old records read back with the default, new records round-trip.
Adding `LastSeenAt` and `ReinforcementCount` to `MemoryEntry` was exactly this. Do not bump a
version for it, and do not write a migration.

**Destructive changes ship with a migration, in the same PR.** Renaming or removing a required
field, restructuring a directory layout, splitting one store into two, changing a file format
— anything a tolerant deserializer cannot absorb. Bump the store's version constant and add an
`ISchemaMigration` that bridges the step.

The line between the two is whether a store written by the *previous* release still reads
correctly under the *new* code. If it does, the change is additive.

### Non-goals

Migrations are **forward-only** — there is no rollback. Deploying an older build against a
migrated store is detected and logged, not undone. They are also **startup-blocking**: the
host does not serve messages until every enrolled store is at the expected version. Neither is
a limitation to design around; both are deliberate for RockBot's workload.

## The version marker

Each enrolled store carries a `.rockbot-schema` file in its root directory:

```json
{
  "store": "memory",
  "version": 1,
  "updatedAt": "2026-09-06T14:22:31.000+00:00"
}
```

The name is dot-prefixed and deliberately has **no `.json` extension**. `FileMemoryStore` and
`FileSkillStore` build their indexes by enumerating `*.json` recursively beneath their root,
and `FileFeedbackStore` does the same for `*.jsonl`; a marker matching those patterns would be
picked up and reported as a corrupt record. (`EmbeddingCache` already writes `.embeddings/`
into the memory root for the same reason.)

The `store` field is a collision check. `FileWispExecutionLog` roots itself at
`WispOptions.SharedVolumePath` — `/rockbot/shared` on the reference deployment — which other
things write into. If the runner finds a marker naming a different store, it logs a warning and
skips that store rather than migrating against someone else's version number.

Writes go through `AtomicFile.WriteAllTextAsync`, so an interrupted stamp leaves either the old
marker or the new one, never a truncated file.

## Startup policy

`SchemaMigrationService` is registered as the first `IHostedService` in `AddRockBotHost`, so its
`StartAsync` runs before any store that has one. For each registered `StoreSchemaDescriptor`:

| On disk | Action |
| --- | --- |
| No marker, directory empty or absent | Stamp `CurrentVersion` — a new store |
| No marker, directory holds data | Assume `LegacyVersion` (default 1), run pending migrations |
| `marker.Version < CurrentVersion` | Run migrations in order, re-stamp after **each** step |
| `marker.Version == CurrentVersion` | Nothing to do |
| `marker.Version > CurrentVersion` | Log a warning, leave untouched, continue |
| `marker.Store` names a different store | Log a warning, skip |
| Marker present but unreadable | Treat as unmarked and re-derive from `LegacyVersion` |

Walking the chain, the runner looks for the single migration whose `FromVersion` matches where
the store currently is. A **gap** (no migration bridges the step) or an **ambiguity** (two
migrations claim the same step) throws and aborts startup: a store this build cannot read
safely is worse than a host that refuses to start, because the agent would otherwise carry on
writing new-format records over data the migration was meant to convert.

A migration that throws propagates the same way. Because the marker is stamped after *each*
step rather than once at the end, a failure three steps in leaves the store at the last step
that actually completed, and the restart resumes from there. Write migrations to be safe to
re-run against partially converted data anyway.

### Ordering caveat

.NET constructs the entire `IEnumerable<IHostedService>` before starting any of it, so other
hosted services' **constructors** have already run when migrations start. This is why
`SchemaMigrationContext` hands a migration a *path* rather than the store service: a migration
that resolved its store could observe, or cache, pre-migration data.

All four framework stores index lazily (`FileMemoryStore` and `FileSkillStore` build their
index on first use; `FileWispExecutionLog`'s constructor only creates its directory), so no
enrolled store reads data before migrations run. A consumer that resolves a store *outside* the
host's startup path is outside this guarantee.

## Writing a migration

```csharp
internal sealed class MemoryV1ToV2 : ISchemaMigration
{
    public string StoreName => AgentMemoryExtensions.MemoryStoreSchemaName;
    public int FromVersion => 1;
    public int ToVersion => 2;

    public async Task MigrateAsync(SchemaMigrationContext context, CancellationToken ct = default)
    {
        foreach (var file in Directory.EnumerateFiles(context.StorePath, "*.json", SearchOption.AllDirectories))
        {
            // Read, reshape, rewrite. Idempotent where you can manage it.
        }
    }
}
```

Register it alongside the version bump:

```csharp
builder.AddSchemaMigration<MemoryV1ToV2>();
```

and bump `MemoryStoreSchemaVersion` in `AgentMemoryExtensions` from 1 to 2 in the same change.
The two always move together — a bump without a migration fails startup on the next upgrade,
and a migration without a bump never runs.

## Enrolling a store

A store is enrolled by the same extension method that registers it, so a consumer that never
opts into a store never gets a marker for it:

```csharp
builder.AddStoreSchema(
    storeName: "my-store",
    currentVersion: 1,
    resolvePath: sp => sp.GetRequiredService<IOptions<MyOptions>>().Value.BasePath);
```

`storeName` is part of the on-disk format — it is written into every marker, so changing it
after release orphans them. `legacyVersion` (default 1) is what an unmarked store holding data
is assumed to be at.

The framework enrols four stores, all currently at version 1 with no migrations:

| Store name | Registered by | Root |
| --- | --- | --- |
| `memory` | `WithLongTermMemory` | `MemoryOptions.BasePath` |
| `skills` | `WithSkills` | `SkillOptions.BasePath` |
| `feedback` | `WithFeedback` | `FeedbackOptions.BasePath` |
| `wisp` | `AddWisps` | `WispOptions.SharedVolumePath` |

## Operating

`SchemaMigrationOptions` tunes the startup check:

```csharp
builder.ConfigureSchemaMigrations(o => o.DryRun = true);
```

- `DryRun` logs every migration it *would* run and writes nothing — boot a copy of a deployment
  against it to see what an upgrade will do before committing to it.
- `Enabled = false` skips the check entirely. Only for a host that migrates its stores by some
  other means; the agent then reads whatever is on disk, unmigrated and unmarked.

## Open

- An operator CLI (`dotnet rockbot migrate --dry-run`) to inspect pending migrations without
  booting the agent. Dry-run currently requires a host start.
- End-to-end migration tests against committed fixtures of old on-disk state. Nothing to
  exercise them with until the first real migration ships.
