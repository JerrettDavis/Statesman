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

Appends are globally serialized, not per-address: allocating a record's position, writing its history file, and appending the change-log line happen inside one critical section so the change feed cannot skip a record. With the default `FlushToDisk = true` that is one `WriteThrough` fsync of global serialization per append. This is a deliberate trade for a provider whose audience is single-writer, and it is what makes an in-process feed reader correct: the gate orders a reader and the writer only within one process. A reader in another process is a separate case — it never contends for that in-process gate at all — and is admitted by the change log's file-sharing mode (`FileShare.ReadWrite` on both the append and the read), so it sees records in commit order without the writer failing underneath it. What it is not guaranteed against is a torn final line: the log has no length guard (see below), so a cross-process reader must treat a parse failure on the last line as "retry later", not as corruption. The provider's writer side remains single-process regardless — the position counter that allocates `GlobalPosition` lives in memory, so only one process may append to a given store's directory.

## Redis

Uses a head value, revision guard, sorted history set, and a server-side append script. It supports distributed optimistic writers that share one Redis authority.

```csharp
builder.UseRedisStore("shared", multiplexer, options =>
{
    options.KeyPrefix = "my-product";
});
```

The global position counter may contain gaps after failed optimistic transactions. Positions remain monotonic but should not be interpreted as a count of successful records.

Redis implements `IStateLedgerReplica`, so it can serve as a tiered hot replica or as a restore target for `Statesman.Tooling`. An import writes the exact record — same revision, same global position — into the head, revision guard, history set, change feed, and partition hash in one transaction guarded by the stream's revision key, replacing any member that already holds that revision or position rather than duplicating it. Before that transaction it raises the store's global position counter to at least the imported position, so a later append never reuses an imported position. Imports are refused above global position 2^52 (`RedisStateLedgerStore.MaxImportablePosition`) because sorted-set scores are IEEE doubles, exact only to 2^53 — a filesystem export, whose positions are UTC ticks, cannot be restored into Redis.

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

All five providers implement `IStateChangeFeed`, giving callers a cursor-resumable, cross-stream view of a store's history. The guarantee is the same on all: the feed is lossless within retention.

The feed is lossless within retention on every provider: records are yielded in ascending position order, and a consumer that resumes from the cursor of the last record it accepted eventually sees every record the store still retains at a higher position. A record may be yielded more than once across calls, so consumers must be idempotent — this is at-least-once, never at-most-once.

Every provider reaches that by allocating a record's `GlobalPosition` in the same step that makes the record visible to the feed. In-memory allocates, appends to the stream, and enqueues under one lock. The filesystem provider allocates, writes the history file, and appends the change-log line under one global gate, writing the head file outside it. Redis allocates and performs all five writes inside one Lua script, so a rejected append burns no position and Redis positions are dense. Entity Framework Core allocates from the `StatesmanSequences` row inside the same serializable transaction as the record insert. The tiered provider inherits its cold tier's guarantee, and its cursors carry cold's positions. A write in flight holds back the records committed after it, so the feed's tail can briefly lag the store's newest record — it lags; it does not skip.

On the filesystem provider this costs real append throughput: every append now serializes behind one `WriteThrough` fsync instead of running per-address in parallel, roughly 0.1–10 ms of global serialization per append at the default `FlushToDisk = true`. Measured at ~1.9 ms/append, and that cost no longer scales with writer count at all — 8 and 16 concurrent writers reach the same append-per-second ceiling, because every writer now serializes behind the same global gate regardless of how many addresses they touch. That is the accepted price of making the change-log line the commit point, which is the only channel this provider has to a reader in another process. The change-log append is not fsynced even at `FlushToDisk = true` — only the history and head writes are — so the durability gap is wider than one write: after a host or power failure, every change-log line the OS had not yet flushed back can be missing while its history file survives, and that window can span many records and many seconds, not just the one write between them. A process crash without a host or power failure does not widen it, since the OS-level page cache survives a process exit. No allocation strategy closes this gap; it is a durability property of the log, not a concurrency one.

Retention is the remaining feed caveat, and it differs by provider because each one trims a different structure. Redis's `PruneAsync` touches only the per-address history sorted set and never the change-feed sorted set, so **Redis has no prune loss** — its feed retains everything and grows without bound. The in-memory provider is the same: `PruneAsync` rewrites the stream's record list and never the change queue, so it has **no prune loss** and the same unbounded growth, which is a real regression against `MaxRevisions`/`MaxBytes`, options that existed specifically to bound memory. The filesystem provider **does** lose pruned records from the feed: prune deletes the history file, the change-log line survives as a dangling entry, and `ReadAsync` skips silently past it. Entity Framework Core **does** too: `PruneAsync` deletes from `StatesmanRecords`, the same table `ReadAsync` queries. Coordinating retention with the feed is a separate follow-up.

No provider currently supports paging or a batch size on `ReadAsync` — Entity Framework Core streams its underlying query, but Redis and in-memory both materialize the full since-cursor result set before the first record is yielded. A consumer resuming after a long gap should expect the whole backlog to load in one call.

`StateChangeCursor` carries no store identity. Passing a cursor obtained from one store into a different store's `ReadAsync` is not rejected — it silently returns whatever slice that position happens to mean for the second store.

## Partition catalog semantics and limitations

The filesystem provider's `IPartitionCatalog` implementation is not atomic with the record write in one respect: `AppendAsync` and `ImportAsync` write the record's history file and then append a line to the change-feed log file that `ListPartitionsAsync` reads from, both inside one critical section, and write the head file afterwards outside it. A crash or I/O failure in the window between the history write and the change-log append leaves a durably-committed partition invisible to `ListPartitionsAsync` until the next successful write to that same address re-adds it — the catalog is not permanently wrong, just transiently stale after a crash, and it self-heals on the next write to that address. The in-memory provider's single lock, Entity Framework Core's single transaction, and Redis's single append script (or, for import and distributed capture, its single `MULTI`/`EXEC`) each cover the record and the catalog update together, so none of them has an equivalent window.

The filesystem provider's `ListPartitionsAsync` is also O(total writes ever made to the store), not O(partition count): it scans the entire change-feed log on every call. It holds the same gate that `AppendAsync` and `ImportAsync` use for the whole duration of that scan, which blocks concurrent appends outright while a listing is in progress. This is consistent with the filesystem provider's `IStateChangeFeed.ReadAsync`, which has the identical cost and locking shape.

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

`PositionGap` is a distance in the store's `GlobalPosition` sequence, which is monotonic but not necessarily dense — the filesystem provider seeds positions from UTC ticks, and any provider's sequence has a gap wherever a restore imported at a higher position — so it is never a count of missing records. Use `PartitionsBehind` when you need a count; use `PositionGap` when you need a trend.

The hot tier is populated lazily by reads and writes routed through the tiered store, not by any background replication. A partition that exists on the authority but has never been read or written through this tiered store is counted in `PartitionsBehind` even though no cache maintenance has failed; so is one whose hot import failed (visible on `LastCacheError`) and one written to the authority directly by another process. All three are genuinely "the replica does not have what the authority has", which is what a replication-lag metric should say. A validating read (`TieredStateReadMode.ValidateCold`, the default) repairs the replica for that one address and the next estimate reflects it. A `PreferHot` read repairs an address the replica does not hold at all (it falls through to the cold read on a miss) but not one the replica holds at an older position, which is the case `PartitionsBehind` most often reflects.

Either tier lacking `IPartitionCatalog` makes `EstimateLagAsync` throw `NotSupportedException` rather than report a misleading zero. All five shipped providers implement `IPartitionCatalog`, so this only arises with a custom store. The estimate costs one full catalog enumeration per tier, which inherits each provider's catalog cost — on the filesystem provider that is O(total writes ever made), not O(partition count) — so poll it at a metrics cadence, not on a request path. It also holds one map entry per hot partition in memory for the duration of the call. No OpenTelemetry instrument is registered for it: catalog enumeration is asynchronous and `ObservableGauge` callbacks are not, so the capability is the pollable source a hosted sampler can feed a gauge from.

## Export and restore semantics and limitations

`Statesman.Tooling` exports through `IPartitionCatalog` plus per-partition `ReadHistoryAsync` and restores through `IStateLedgerReplica.ImportAsync`, so its guarantees are exactly those capabilities' guarantees composed — see the [Backup and restore](../guides/backup-restore.md) guide for the operator view.

Every shipped provider can be an export source. In-memory, filesystem, Redis, and Entity Framework Core can be restore targets; the tiered store cannot (restore into its cold store). An export is complete for every revision the source retains, in canonical partition order and ascending revision order, but is not a cross-partition point-in-time snapshot — each partition's history is one read, and writes landing on other partitions during the export may or may not be included. The filesystem provider's catalog window after a crash (see "Partition catalog semantics and limitations" above) applies to exports from it. Export reads full history explicitly (`Take = null`); the `StateHistoryOptions` default of 100 would otherwise truncate long streams silently.

Restore validates the whole file — format, root, fingerprint, every record, trailer count — before contacting the target and refuses a target that already holds the root unless `AllowNonEmptyTarget` is set, because `GlobalPosition` values from independent histories collide: Entity Framework Core's unique index on `GlobalPosition` rejects a collision mid-import, the in-memory and filesystem providers interleave the two histories in their change feeds without error, and Redis overwrites the target's record at the colliding position. Every provider's `ImportAsync` is exact and idempotent, so re-running a restore that failed store-side is safe. Each provider advances its position counter to at least the highest imported position (in-memory and filesystem in memory, Entity Framework Core in its sequence row, Redis by a max-advance script run before the import transaction), so later appends never reuse an imported position. The change feed and partition catalog of the target reflect imported records at their original positions on every provider that accepts them; Redis refuses positions above 2^52 rather than storing them inexactly (see the Redis section above).

## Outbox delivery semantics and limitations

`Statesman.Outbox` reads a store's `IStateChangeFeed` from a persisted cursor and publishes to an `IStateChangeSink`, composing that capability's own guarantees rather than adding new ones — see the [Outbox delivery](../guides/outbox.md) guide for the operator view. The composed promise, stated identically there and on `StateChangeDispatcher`'s XML doc: every record the outbox reads from the feed is handed to the sink at least once, and the persisted cursor never advances past a record the sink has not accepted.

Per-provider suitability as an outbox source follows directly from "Change feed semantics and limitations" above. Any provider with `StateRetentionPolicy.KeepAll` is a complete configuration: the feed is lossless within retention on all five, so a `KeepAll` store loses nothing to either caveat. The filesystem provider carries one residual — a crash between the record write and the change-log append leaves that record permanently absent from the feed — so a deployment that must not lose a record across a host crash should prefer Entity Framework Core or Redis. Any provider with a configured (non-`KeepAll`) retention policy inherits prune loss on the two providers whose feed structures retention actually trims, filesystem and Entity Framework Core, because `StateHandle` prunes after every successful append; on Redis and in-memory the feed is not trimmed at all, so retention does not cost the outbox records there (it costs unbounded feed growth instead). The tiered provider reads its change feed from its cold tier and takes its outbox lease hot-first, so an outbox over a tiered store must key its cursor by the tiered store's own name, not its cold store's — the two are different `IStateLedgerStore.Name` values with unrelated cursor positions.

Only Redis and Entity Framework Core implement `IStateLeaseProvider`, which is what the outbox's default `RequireLease = true` requires to keep one dispatcher at a time advancing the cursor. An outbox over the filesystem or in-memory provider needs `RequireLease = false`, accepting that a second dispatcher can advance the cursor past records neither of them published.

The Redis stream sink's entry-id contract is `{globalPosition}-0`. This is exact for every `long` position, because a Redis stream entry id is two unsigned 64-bit integers, and it is explicitly **not** subject to `RedisStateLedgerStore.MaxImportablePosition` (2^52), which bounds the ledger's own sorted-set change-feed scores, not stream entry ids — a filesystem export's UTC-tick positions, too large to restore into Redis's ledger, publish to a Redis outbox stream without truncation. `GlobalPosition` is unique only within one store's own position lineage, so a stream key must be exclusive to one store: the sink verifies a rejected `XADD` is genuinely this message republished (by comparing the existing entry's `messageId`) before counting it as a duplicate, and throws rather than silently discarding a message when the position instead belongs to a different store's lineage sharing that key.

There is a known `MessageId` collision case, documented rather than fixed: `StateAddress` equality is ordinal on `Root` while `Canonical` lower-cases it, so a store written under both `"App"` and `"app"` has one revision sequence on the in-memory and filesystem providers (whose per-address gate keys on the canonical address) but two independent revision sequences on Entity Framework Core (which keys on the exact root). The outbox's `MessageId` is built from the canonical address, so those two Entity Framework Core sequences can produce colliding message ids.

Entity Framework Core cursor storage is deliberately not shipped: an existing Entity Framework Core consumer would need a second migration this soon after `StatesmanLeases`, so an Entity Framework Core-backed outbox uses the filesystem or Redis cursor store, or a consumer-supplied `IOutboxCursorStore`, instead.
