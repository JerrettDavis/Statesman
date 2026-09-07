# Backup and restore

`Statesman.Tooling` exports one root's retained ledger history from a store to a portable file and restores it exactly — same revisions, same global positions, same timestamps — into another store serving the same declaration. It is the disaster-recovery and environment-cloning tool; for seeding test scenarios with *logical* state that gets new revisions, use `StateFixture` from `Statesman.Testing` instead (see [Testing](testing.md)).

## Export

```csharp
IStateLedgerStore store = services.GetRequiredService<IStateStoreResolver>().Resolve("cold");
IStatesman statesman = services.GetRequiredService<IStatesman>();

StateLedgerExportSummary summary = await StateLedgerExport.ExportToFileAsync(
    store,
    statesman.Manifest,
    "backups/app-2026-09-04.jsonl",
    new StateLedgerExportOptions
    {
        Metadata = new Dictionary<string, string> { ["environment"] = "production" },
    });
```

`ExportAsync` takes any writable `Stream` instead of a path. Both return the number of records and partitions written.

What an export contains:

- every revision the source store **retains** — post-retention history, exactly what `ReadHistoryAsync` returns — for every partition the store's `IPartitionCatalog` lists under the manifest's root when enumeration begins;
- partitions ordered by canonical address, revisions ascending within each partition, so two exports of a quiet store differ only in the header timestamp;
- the declaration fingerprint (`StatesmanManifest.Fingerprint`) and the format version (`statesman.ledger-export/v1`) in the first line, and record and partition counts in the last line.

What it does not promise:

- **A cross-partition point-in-time snapshot.** Each partition's history is read as one call, but a write that lands on another partition while the export runs may or may not appear. Quiesce writers — or export from a cold store no writer is touching — for a consistent backup.
- **Records under other roots.** A store shared by several roots exports one root per call.
- **Anything the change feed promises.** Export deliberately reads the partition catalog and per-partition history rather than `IStateChangeFeed`. The feed is lossless within retention, so that is no longer about losing a record — it is about the feed's provider-specific prune behavior, which would make an export's contents depend on which structures each provider's retention happens to trim. The filesystem provider's catalog has its own post-crash window (see [Providers](../providers/index.md)): a partition missing from that catalog is missing from the export until the next write to it.

A source store without `IPartitionCatalog` throws `NotSupportedException`. All five shipped providers implement it.

## Restore

```csharp
IStateLedgerStore target = services.GetRequiredService<IStateStoreResolver>().Resolve("cold");

StateLedgerRestoreSummary summary = await StateLedgerRestore.RestoreFromFileAsync(
    target,
    statesman.Manifest,
    "backups/app-2026-09-04.jsonl");
```

Restore reads and validates the **entire** file before contacting the target, then checks the target, then imports. The whole export is held in memory for the duration of the restore, so size the restoring process for the export, not just for the target store. It refuses — by throwing `StateLedgerRestoreException` with nothing imported — when:

- the header's format is not `statesman.ledger-export/v1`;
- the header's root does not match `manifest.Id`;
- the header's fingerprint does not match `manifest.Fingerprint` — history recorded under a different declaration is never imported, even partially;
- any record line whose `root` differs from the header's root;
- any record line fails `StateRecord.Validate()`, or the file is truncated (no trailer, or a trailer whose record count disagrees with the lines present);
- the target already holds any partition under the root (see below).

It throws `NotSupportedException` when the target lacks `IStateLedgerReplica` (needed to import exact records) or, unless `AllowNonEmptyTarget` is set, `IPartitionCatalog` (needed to check the target is empty), or when the target is the Redis provider and the export contains a global position above 2^52 (see below). Every shipped provider implements both **except the tiered store**, which deliberately has no `IStateLedgerReplica` because its cold store is authoritative: restore into the cold store directly, and the hot replica repairs itself through normal validating reads.

### Why a non-empty target is refused by default

Restore preserves `GlobalPosition`. Positions from two independent histories collide: the Entity Framework Core provider's unique index would reject the collision **mid-import**, the in-memory and filesystem providers would silently interleave two unrelated histories in their change feeds, and the Redis provider would overwrite the target's record at the colliding position. So by default restore requires the target's catalog to list no partition under the root. Set `StateLedgerRestoreOptions.AllowNonEmptyTarget` for the two cases where that is intended:

- **Re-running after a store-side failure mid-import.** Validation failures never touch the target, but a store error (connection loss, disk full) after import has begun leaves the records imported so far in place. Every `ImportAsync` is exact and idempotent, so re-running the same restore with `AllowNonEmptyTarget = true` finishes the job without duplicating anything.
- **Restoring over the same lineage.** A store that already holds positions from the same original history — a replica, or the original store after partial data loss — shares the export's position sequence, and exact import repairs it in place.

After a restore, the target's position counter is at or above the highest imported position on every provider, so later appends never reuse an imported position.

The Redis provider refuses to import any global position above 2^52 (`RedisStateLedgerStore.MaxImportablePosition`) with `NotSupportedException`: its change feed is a sorted set scored by position, sorted-set scores are IEEE doubles that are exact only up to 2^53, and an inexact score would silently collide with neighbouring records. The filesystem provider allocates positions from UTC ticks, far above that bound, so a filesystem export cannot be restored into Redis — restore it into the in-memory, filesystem, or Entity Framework Core provider instead. The refusal is raised by the first offending record; every position in a filesystem export is above the bound, so nothing is imported before it.

### Schema versions and payloads

Restore never deserializes payloads. Each record's `SchemaVersion` is carried verbatim, and the declaration's schema migrations apply at read time exactly as they would on the original store. The fingerprint check guarantees the declaration is the same one, so the migrations are too.

## Format

The file is newline-delimited JSON (camelCase, enums as strings, nulls omitted). Line one is a `StateLedgerExportHeader`, the last line is a `StateLedgerExportTrailer`, and every line between is a `StateLedgerExportRecord` with the address flattened to `root`/`path`/`partition` and the payload base64-encoded. `StateLedgerExportFormat.Json` exposes the serializer options for tools that read exports directly.
