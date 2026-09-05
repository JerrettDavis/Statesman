# Ledger providers

The runtime resolves providers by the static name declared on each state. All providers implement the same latest-read, history-read, conditional append, and prune contract.

## In memory

Built into `Statesman`. It is the default provider and the reference behavior for tests. Data disappears with the process.

```csharp
services.AddStatesman(declaration, builder => builder.UseInMemoryStore("volatile"));
```

## Filesystem

Stores a head file and immutable revision files under a SHA-256 stream directory. Writes use temporary files and atomic replacement. It is suitable for desktop tools, agents, local-first applications, and development environments where one process owns the directory.

```csharp
builder.UseFileSystemStore("local", "./data/statesman");
```

Process-local semaphores protect concurrent callers in one process. The provider is not a cross-machine lock service. Do not point several independent writers at the same ordinary network directory.

## Redis

Uses a head value, revision guard, sorted history set, and transaction conditions. It supports distributed optimistic writers that share one Redis authority.

```csharp
builder.UseRedisStore("shared", multiplexer, options =>
{
    options.KeyPrefix = "my-product";
});
```

The global position counter may contain gaps after failed optimistic transactions. Positions remain monotonic but should not be interpreted as a count of successful records.

Redis implements `IStateLedgerReplica`, so it can serve as a tiered hot replica or as a restore target for `Statesman.Tooling`. An import writes the exact record — same revision, same global position — into the head, revision guard, history set, change feed, and partition hash in one transaction guarded by the stream's revision key, replacing any member that already holds that revision or position rather than duplicating it. Before that transaction it raises the store's global position counter to at least the imported position, so a later append never reuses an imported position.

## Entity Framework Core

Provides a base `StatesmanLedgerDbContext`, head table, append table, and sequence table. Create a derived context, register an `IDbContextFactory<TContext>`, add a migration, and select the store by name.

```csharp
services.AddPooledDbContextFactory<AppStateDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.UseEntityFrameworkStore<AppStateDbContext>("relational");
```

Append uses a serializable transaction plus EF concurrency tokens. Provider-specific production testing remains important because lock and isolation behavior differs by relational database.

## Tiered hot/cold

The cold provider is authoritative. Successful cold records are imported into a hot provider that implements `IStateLedgerReplica`. Exact records are imported, so the cache does not invent a second revision or global position.

```csharp
builder
    .UseInMemoryStore("hot")
    .UseFileSystemStore("cold", "./data")
    .UseTieredStore("tiered", "hot", "cold");
```

History reads always go to cold storage. By default, head reads validate against cold storage and repair the hot replica. `PreferHot` is an explicit eventual-consistency option that returns a cached head without checking for a newer cold revision. A hot-cache failure is recorded on the tiered provider and does not reject an authoritative cold commit. `ServeHotWhenColdUnavailable` is separately opt-in because availability fallback can return an older value.

## Lease semantics and limitations

Redis and Entity Framework Core implement `IStateLeaseProvider`, giving `StatesmanRuntime` maintenance a distributed lease it can use to coordinate interval refresh across replicas. The two implementations enforce mutual exclusion differently, and each carries its own caveat.

Redis's `IStateLeaseProvider` implementation is single-instance Redis consistency only. It is not a Redlock/quorum implementation and does not survive a Redis failover or split-brain the way a quorum-based lock would.

Entity Framework Core's `IStateLeaseProvider` implementation evaluates lease expiry against the acquiring process's own clock (`TimeProvider`), not a database-server-enforced TTL the way Redis's is. Clock skew between replicas is therefore a real (if small, at the default 30-second maintenance lease TTL) property of this provider's mutual exclusion — operators running EF Core-backed multi-replica deployments should keep replica clocks synchronized (e.g. NTP).

## Change feed semantics and limitations

All five providers implement `IStateChangeFeed`, giving callers a cursor-resumable, cross-stream view of a store's history. The guarantee is not identical everywhere.

`GlobalPosition` is allocated before the record is durably visible on Redis, the filesystem provider, and the in-memory provider — only Entity Framework Core allocates and commits the position inside the same transaction. A consumer reading while one address's append is still in flight, but a later position has already landed, can persist a cursor that permanently skips the earlier record: treat this feed as at-least-once with possible tail loss under concurrent writes to different addresses, not as lossless, until a future release closes this gap.

Pruning a stream and the change feed are not consistently coordinated today. Entity Framework Core's feed loses pruned records silently (it queries the same table `PruneAsync` deletes from). The filesystem provider's index file keeps a dangling entry that is silently skipped on read. Redis's and the in-memory provider's change-feed structures are not trimmed at all and retain history forever regardless of the store's own retention policy.

No provider currently supports paging or a batch size on `ReadAsync` — Entity Framework Core streams its underlying query, but Redis and in-memory both materialize the full since-cursor result set before the first record is yielded. A consumer resuming after a long gap should expect the whole backlog to load in one call.

`StateChangeCursor` carries no store identity. Passing a cursor obtained from one store into a different store's `ReadAsync` is not rejected — it silently returns whatever slice that position happens to mean for the second store.

## Partition catalog semantics and limitations

The filesystem provider's `IPartitionCatalog` implementation is not atomic with the record write: `AppendAsync` and `ImportAsync` write the record first, then separately append a line to the change-feed log file that `ListPartitionsAsync` reads from. A crash or I/O failure in the window between those two writes leaves a durably-committed partition invisible to `ListPartitionsAsync` until the next successful write to that same address re-adds it — the catalog is not permanently wrong, just transiently stale after a crash, and it self-heals on the next write to that address. The in-memory provider's single lock, Entity Framework Core's single transaction, and Redis's single `MULTI`/`EXEC` each cover the record and the catalog update together, so none of them has an equivalent window.

The filesystem provider's `ListPartitionsAsync` is also O(total writes ever made to the store), not O(partition count): it scans the entire change-feed log on every call. It holds the same gate `AppendAsync`'s change-feed write uses for the whole duration of that scan, which blocks concurrent appends' change-feed writes while a listing is in progress. This is consistent with the filesystem provider's `IStateChangeFeed.ReadAsync`, which has the identical cost and locking shape.

## Distributed capture semantics and limitations

Redis and Entity Framework Core implement `IDistributedCapture`, giving callers a coherent, multi-address snapshot read for both `StateCaptureConsistency.ReadCommittedDistributed` and `StateCaptureConsistency.SnapshotDistributed`.

Redis backs both levels with an unconditional `MULTI`/`EXEC` batch of head reads. Standalone Redis executes commands single-threaded, so this is a true, exact snapshot for either requested level — there is no weaker mechanism to fall back to. Against Redis Cluster, addresses whose keys span more than one hash slot cannot share one `MULTI`/`EXEC` transaction; a `CROSSSLOT` failure — raised client-side by StackExchange.Redis before dispatch, or server-side on the transaction itself, and observable either on `ExecuteAsync` or later on an individual queued read — is translated into `NotSupportedException` rather than silently degrading to unsynchronized sequential reads. Both the scenario and the exception types caught (`RedisCommandException`, `RedisServerException`) are reasoned rather than verified: reproducing a `CROSSSLOT` failure needs a real Redis Cluster deployment, which this repo's test infrastructure (`STATESMAN_TEST_REDIS`, standalone only) does not provide.

Entity Framework Core maps the two levels onto genuinely different database isolation levels — `ReadCommittedDistributed` to `IsolationLevel.ReadCommitted`, `SnapshotDistributed` to `IsolationLevel.Serializable` — so the distinction the caller asks for is the distinction the database enforces, not a cosmetic one. On SQLite that mapping carries a real cost: `Serializable` opens the transaction as `BEGIN IMMEDIATE`, which takes a database-wide RESERVED (write-intent) lock the instant the transaction starts and holds it until commit, so a `SnapshotDistributed` capture of N addresses blocks every other writer in the database for N round trips. This was found and confirmed experimentally while designing this phase's own EF Core lease-race test. `Serializable` is not being changed here — it is strictly sufficient and matches the transaction shape `AppendAsync` and `AcquireAsync` already use — but operators should treat a wide `SnapshotDistributed` capture against SQLite as a latency and availability trade-off, and the mapping is worth revisiting (`IsolationLevel.Snapshot` where a provider offers it) if it becomes a complaint.

The filesystem provider does not implement `IDistributedCapture` at all. It has no cross-process transactional primitive to build a coherent multi-address read on — the same reason it does not implement `IStateLeaseProvider`. The in-memory provider does not implement it either. Requesting a distributed level against either store throws `NotSupportedException` from the runtime. The tiered provider always delegates `CaptureAsync` to its cold store, matching the `IStateChangeFeed` and `IPartitionCatalog` precedent, and throws `NotSupportedException` when the cold store lacks the capability; a capture never reads the hot replica, whose head may be stale.

The coherence guarantee is per-store, and the runtime enforces that boundary rather than papering over it. A `SnapshotDistributed` capture whose requested addresses resolve to more than one store throws `NotSupportedException` before any store is contacted: each store's `IDistributedCapture` is atomic only within itself, so stitching two independently-timed reads together would return a torn view under the name of a point-in-time snapshot. `ReadCommittedDistributed` spans stores freely — its own definition already admits concurrent commits during the capture, which independent per-store reads honestly satisfy. A caller needing snapshot coherence across stores must split the capture per store and reconcile the results itself.

## Replication lag semantics and limitations

Only the tiered provider implements `IReplicationLagSource`. Lag is a property of the hot/cold relationship, not of any single provider, so it is never forwarded from an inner store: `TryGetCapability<IReplicationLagSource>` succeeds on a `TieredStateLedgerStore` and on nothing else.

`EstimateLagAsync` compares the two tiers through their `IPartitionCatalog` capabilities. It enumerates the hot replica's catalog into a per-address head map first, then enumerates the cold authority's catalog, and reports three numbers: `AuthoritativePosition` (cold's newest head `GlobalPosition`, or 0 when empty), `ReplicaPosition` (hot's newest head `GlobalPosition`, or 0 when empty), and `PartitionsBehind` (the number of cold partitions hot either does not hold at all or holds at an older position). `PositionGap` is the clamped difference of the two positions and `IsCaughtUp` is true only when both `PartitionsBehind` and `PositionGap` are zero. Because replica import preserves `GlobalPosition` exactly, a hot head at the same position as its cold head is the same record, so the comparison is exact rather than heuristic — provided the replica's positions come only from imports of this cold tier's records. Writing directly to the store serving as the hot tier breaks that premise: its own counter allocates positions in an unrelated sequence, so a hot head can sit at or above its cold head while holding a different record, and the estimate will not count that partition as behind.

The estimate is deliberately conservative. Hot is read before cold, so a write that lands on the authority while the estimate is in progress makes the reported lag larger than it was at any single instant — never smaller, given accurate catalogs on both tiers. The one shipped catalog that can be inaccurate is the filesystem provider's after a crash (see "Partition catalog semantics and limitations" above): a partition durably committed but not yet re-added to its change-feed log is invisible to that tier's catalog, so with the filesystem provider as the cold tier the estimate can under-report until the next write to that address self-heals the catalog. Treat the result as "at least this far behind", not as a point-in-time snapshot of both tiers.

`PositionGap` is a distance in the store's `GlobalPosition` sequence, which is monotonic but sparse — the Redis provider consumes a position on a failed conditional write, for example — so it is never a count of missing records. Use `PartitionsBehind` when you need a count; use `PositionGap` when you need a trend.

The hot tier is populated lazily by reads and writes routed through the tiered store, not by any background replication. A partition that exists on the authority but has never been read or written through this tiered store is counted in `PartitionsBehind` even though no cache maintenance has failed; so is one whose hot import failed (visible on `LastCacheError`) and one written to the authority directly by another process. All three are genuinely "the replica does not have what the authority has", which is what a replication-lag metric should say. A validating read (`TieredStateReadMode.ValidateCold`, the default) repairs the replica for that one address and the next estimate reflects it. A `PreferHot` read repairs an address the replica does not hold at all (it falls through to the cold read on a miss) but not one the replica holds at an older position, which is the case `PartitionsBehind` most often reflects.

Either tier lacking `IPartitionCatalog` makes `EstimateLagAsync` throw `NotSupportedException` rather than report a misleading zero. All five shipped providers implement `IPartitionCatalog`, so this only arises with a custom store. The estimate costs one full catalog enumeration per tier, which inherits each provider's catalog cost — on the filesystem provider that is O(total writes ever made), not O(partition count) — so poll it at a metrics cadence, not on a request path. It also holds one map entry per hot partition in memory for the duration of the call. No OpenTelemetry instrument is registered for it: catalog enumeration is asynchronous and `ObservableGauge` callbacks are not, so the capability is the pollable source a hosted sampler can feed a gauge from.
