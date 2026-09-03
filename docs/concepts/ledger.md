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
