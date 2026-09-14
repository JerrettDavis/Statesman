# Ledger and retention

Every authoritative state change is an append. The latest value is a projection over the latest retained record, not a separately mutable object.

## Record identity

A stream is identified by:

```text
root :: state path :: partition
```

A record includes state revision, root-wide global position, operation, status, schema version, serialized payload, source, correlation and causation IDs, freshness timestamps, metadata, and an optional structured error.

## Optimistic writes

The runtime hydrates the latest revision and appends with an expected revision. A direct `SetAsync` with an explicit expected revision fails immediately on conflict. Pure updates and interaction reducers without an explicit revision are retried against the newly observed snapshot, up to a bounded attempt count.

This rule prevents accidental last-write-wins behavior. Reducers should therefore be deterministic and free of external side effects. Perform effects before dispatch, after observing the accepted change, or through a separate orchestration layer.

## Global position

Each provider assigns a monotonically increasing global position inside its authority. `CaptureAsync` hydrates requested state, enters the runtime commit gate, and returns snapshots that cannot be interleaved by another in-process commit during capture.

This is an in-process coherence guarantee. A distributed provider can still be changed by another process while a capture is being assembled. A future provider capability may offer a database-native global-position capture for stronger distributed reads.

## Retention

Retention can combine:

- all revisions forever
- most recent revision count
- maximum record age
- maximum retained payload bytes
- optional removal of non-latest clear tombstones

The latest revision is always retained even when it alone exceeds the configured byte budget. Pruning runs after a successful commit. A pruning failure does not roll back the accepted state change and is reported as maintenance failure.

Several limits narrow sequentially rather than intersecting independently: the age cutoff runs first, then the tombstone filter, then the revision count, then the byte budget over whatever survived. A revision count therefore counts what survived the earlier filters, not the raw number of stored revisions.

The age cutoff is inclusive. A revision whose occurrence time is exactly the cutoff is retained, so "keep three hours" retains a revision that is three hours old to the tick.

The byte budget counts serialized payload bytes and nothing else: not the file a provider writes, not the key it stores under, not the JSON envelope, not the metadata. A clear tombstone carries no payload and therefore costs nothing against the budget. A filesystem store's on-disk cost for a retained revision is larger than its payload, sometimes much larger, so size the budget against payloads rather than against disk.

The byte budget admits revisions newest first, and skips past one that would overflow rather than stopping at it. The set it retains is therefore not always a contiguous run: a small old revision can survive while a large newer one is evicted. This is deliberate, and it retains strictly more revisions under the same budget than stopping at the first overflow would.

Pruning an address that was never written is a no-op, not an error, so a caller can prune on a schedule without first proving the address exists. Pruning twice with the same policy removes nothing the first pass did not, because every filter is computed over what is stored now.

## Schema evolution

Each state declares a schema version. A current record is deserialized directly when versions match. Older records require an explicit migration:

```csharp
state
    .SchemaVersion(2)
    .MigrateFrom<UserStateV1>(1, old => new UserState(
        old.Name,
        Permissions: [],
        old.Enabled));
```

Migration is a read projection. Statesman does not rewrite history automatically. This preserves lineage and lets retention or an explicit compaction process decide when old payloads disappear.
