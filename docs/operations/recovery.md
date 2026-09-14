# Recovery and corruption

This page is for a store that will not read, will not export, or reads something wrong. It covers
what each provider can lose, how to see the damage, and what to do about it. **Every procedure below
starts by stopping the writer and taking a copy of the store.** Nothing on this page is safe to run
against a live writer: a finding taken from a store being written can describe a normal mid-append
state rather than damage, and several of the repairs below write to disk.

## Before anything: take a copy

For the filesystem provider, copy the whole root directory before touching anything else. For the
other providers, use the provider's own backup mechanism, or `Statesman.Tooling`'s export (see
[Backup and restore](../guides/backup-restore.md)).

An export taken from a damaged filesystem store can be **incomplete without erroring**. A stream with
no head file is skipped by the partition catalog the export enumerates, which is exactly what the
`MissingHead` finding below reports — take the raw directory copy first, and only then attempt an
export.

## Filesystem

### What is on disk

| Path | Holds |
|---|---|
| `<root>/<first two hex>/<full hex>/head.json` | the latest record for the stream |
| `<root>/<first two hex>/<full hex>/history/<revision padded to 20>.json` | one immutable file per retained revision |
| `<root>/_changes.log` | the append-only change feed, five tab-separated fields per line |
| `<root>/_changes.gen` | the compaction generation counter |
| `<file>.<32 hex>.tmp` | a transient file mid write, next to wherever `<file>` would land |

The hex is the SHA-256 of the address's canonical form. The change-log index is **in process only and
never written to disk** — there is no index file to delete or rebuild.

### The sequence

```csharp
await using var store = new FileSystemStateLedgerStore(
    "recovery", new FileSystemStateLedgerStoreOptions { RootDirectory = "/var/lib/app/statesman" });

// 1. See. Writes nothing.
FileSystemLedgerVerificationReport verification = await store.VerifyAsync();
foreach (FileSystemLedgerFinding finding in verification.Findings)
{
    Console.WriteLine($"{finding.Kind} {finding.Path} — {finding.Detail}");
}

// 2. See what would change. Also writes nothing: the parameterless overload is a dry run.
FileSystemLedgerRepairReport planned = await store.RepairAsync();

// 3. Apply.
FileSystemLedgerRepairReport applied = await store.RepairAsync(dryRun: false);

// 4. See again. One repair can expose the next finding.
FileSystemLedgerVerificationReport after = await store.VerifyAsync();
```

Step 2's `ChangeLogLinesDropped` can be **higher** than step 3's. A dry run does not restore the
history files that would make some of those change-log lines dereference again, so it counts lines a
real repair — which restores record files before it compacts the log — goes on to heal instead of
drop.

### What each finding means, and what repair does about it

| Kind | What it costs you today | What repair does |
|---|---|---|
| `TornChangeLogTail` | the final unparseable line is skipped silently by every read path; compaction copies it through verbatim | Nothing to do. The next append turns it into a reportable `MalformedChangeLogLine`. Left deliberately: it is the crash marker. |
| `MalformedChangeLogLine` | every read of the change feed throws, naming the line | Nothing, and the whole change-log half of repair is skipped. Stop the writer, copy `_changes.log`, truncate at the reported line, and re-run verify. Repair will not do this for you because the line may name a record that exists. |
| `DanglingChangeLogLine` | skipped silently on every read | Drops the line, through change-log compaction — the history file is provably gone, so the line provably dereferences to nothing. |
| `ChangeLogPositionMismatch` | the record is yielded twice, with the two copies' positions disagreeing | Drops the line, through the same compaction — the history file is authoritative. |
| `MissingHead` | reads by address still work, but the address vanishes from the partition catalog and therefore from every export | Writes `head.json` back from the highest-revision history file — the value a read by address already computes at read time. Creates a file, destroys none. |
| `MissingHistoryFile` | the head still answers; the log line for that revision reads as a `DanglingChangeLogLine` | Writes `history/<R:D20>.json` back from the head record. Safe because pruning can never delete the latest revision's history file. |
| `UnreadableRecordFile` | an uncapped feed read throws for the entire store; a history read for that address throws too | Quarantines it: renames it to `<file>.corrupt` (`.corrupt.<n>` on collision), taking it out of the enumeration a read walks. The bytes stay on disk under the new name. Note the ordering: the rename happens before the change-log half, so that revision's line becomes a `DanglingChangeLogLine` and is dropped too whenever the same pass compacts at all. |
| `OrphanedTemporaryFile` | invisible to every read path | Nothing. With the writer confirmed stopped, delete the listed paths yourself. Repair will not, because a temporary file is indistinguishable from a live in-flight write. |
| `MisplacedStreamDirectory` | every read by address misses it; only a directory walk finds it | Nothing. With the writer stopped, move the directory to the path the finding names, or export the stream and restore it. Repair will not move a directory. |

Two things the table above does not make obvious:

- `DanglingChangeLogLine` is reported **only** when the history file is absent at its canonical path.
  A file that exists but fails to deserialize is `UnreadableRecordFile` instead, and repair quarantines
  it by rename rather than dropping its change-log line for being unreadable. Be aware of the ordering,
  though: the quarantine rename runs before the change-log half, so if the same pass compacts at all —
  which it does whenever any dangling or position-mismatched line exists anywhere in the store — that
  revision's line is then genuinely dangling and is dropped with the rest. When nothing else is
  droppable the pass skips compaction and the line survives, and the next `VerifyAsync` reports it as
  `DanglingChangeLogLine`. Either way the quarantined bytes stay on disk under the `.corrupt` name.
- A `MisplacedStreamDirectory` is reported and never moved. If a change-log line's record lives only
  inside such a directory, the record is not at its canonical path, so the line reads as
  `DanglingChangeLogLine` and repair's compaction drops it — the reader already skipped it, so nothing
  observable changes for a normal read. The misplaced directory and its bytes stay on disk exactly
  where they were; recovering that record means finding the directory by hand and moving it before
  compaction runs, not after.

### Change-log compaction

`CompactChangeLogAsync` rewrites `_changes.log` to drop lines that provably dereference to nothing,
which keeps both the file's size and a reader's per-line scan cost bounded as a store ages.
`RepairAsync` calls it for you whenever there is a droppable line, so you do not need to call it
separately as part of this sequence. Like every write this provider makes to the log, it must run in
the writer's own process — see [Providers](../providers/index.md) for the cross-process rename
caveats that follow from that.

### What recovery cannot do

This provider is **single-writer by design**: the counter that allocates `GlobalPosition` lives in
memory, so two processes appending to one directory is unsupported, and no repair here makes it
supported.

A quarantined record is **gone from the feed and from history** the moment it is renamed. Its bytes
are on disk beside the original name with a `.corrupt` suffix, and recovering anything usable from
them — hand-editing the JSON, restoring it under a new revision — is a manual job this page does not
walk through.

## Entity Framework Core

Schema upgrades run through migrations, not through this page — see
[Entity Framework Core migrations](../providers/entity-framework-core-migrations.md), including the
baseline history row a consumer already running under `EnsureCreated` needs before adopting a shipped
migration package.

For data damage, the answer is the database engine's own restore, followed by a
`Statesman.Tooling` import of anything the engine-level restore left short. The unique index on
`GlobalPosition` is what makes a colliding restore fail loudly, mid-import, rather than silently
interleaving two histories.

## Redis

`Statesman.Tooling` import is the recovery path. It refuses to write into a non-empty target unless
`AllowNonEmptyTarget` is set, refuses any position above 2^52 outright rather than storing it
inexactly, and removes the stale change-feed entry it replaces by member rather than duplicating it.
See [Providers](../providers/index.md) for the full import contract.

Redis's own persistence (RDB or AOF) is the backup story here — Statesman adds no recovery tooling of
its own for damage to Redis's on-disk files.

## In memory and tiered

Nothing to recover. The in-memory provider has no durable form at all, and a tiered store's authority
is its cold tier — recovering a tiered deployment means recovering whichever provider is configured as
cold, by that provider's section above.

## Upgrading a store's format

No shipped provider has changed its stored format within `0.x`. The filesystem layout described above
is the only textual, human-inspectable one; every other provider's format is whatever its own storage
engine uses. The supported move across a format change, when one ships, is the same as the supported
move across environments today: export with `Statesman.Tooling` on the old version, import on the
new one. See [Backup and restore](../guides/backup-restore.md).
