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

Appends are globally serialized, not per-address: allocating a record's position, writing its history file, and appending the change-log line happen inside one critical section so the change feed cannot skip a record. With the default `FlushToDisk = true` that is two `WriteThrough` fsyncs of global serialization per append (the history file and the change-log line). This is a deliberate trade for a provider whose audience is single-writer, and it is what makes an in-process feed reader correct: the gate orders a reader and the writer only within one process. A reader in another process is a separate case — it never contends for that in-process gate at all — and is admitted by the change log's file-sharing mode (the reader opens the log with `FileShare.ReadWrite`, which is compatible with the writer's append in either opening order), so it sees records in commit order without the writer failing underneath it. What it is not guaranteed against is a torn final line: the log has no length guard (see below), so a cross-process reader must treat a parse failure on the last line as "retry later", not as corruption. The provider's writer side remains single-process regardless — the position counter that allocates `GlobalPosition` lives in memory, so only one process may append to a given store's directory.

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

Both implementations agree on what "lost" means for `IStateLease.RenewAsync`, and the interface now says so: a lease is lost once its TTL has lapsed **or** once another holder has acquired it, and renewal returns `false` in both cases rather than resurrecting an expired lease nobody else happened to take. Redis gets this from the server deleting the key at its TTL; Entity Framework Core checks `ExpiresAt` against the same `TimeProvider` its `AcquireAsync` judges expiry by, and reports a lease lost to a concurrent re-acquisition as `false` rather than surfacing the row's concurrency-token conflict.

## Change feed semantics and limitations

All five providers implement `IStateChangeFeed`, giving callers a cursor-resumable, cross-stream view of a store's history. The guarantee is the same on all: the feed is lossless within retention.

The feed is lossless within retention on every provider: records are yielded in ascending position order, and a consumer that resumes from the cursor of the last record it accepted eventually sees every record the store still retains at a higher position. A record may be yielded more than once across calls, so consumers must be idempotent — this is at-least-once, never at-most-once.

Every provider reaches that by allocating a record's `GlobalPosition` in the same step that makes the record visible to the feed. In-memory allocates, appends to the stream, and enqueues under one lock. The filesystem provider allocates, writes the history file, and appends the change-log line under one global gate, writing the head file outside it. Redis allocates and performs all five writes inside one Lua script, so a rejected append burns no position and Redis positions are dense. Entity Framework Core allocates from the `StatesmanSequences` row with a single atomic increment taken as the first statement of the append transaction, so the row's write lock is held for the whole append and positions are handed out in commit order; the transaction itself runs at read-committed isolation, because that one row is the serialization point rather than the isolation level. The tiered provider inherits its cold tier's guarantee, and its cursors carry cold's positions. A write in flight holds back the records committed after it, so the feed's tail can briefly lag the store's newest record — it lags; it does not skip.

On the filesystem provider this costs real append throughput: every append now serializes behind `WriteThrough` fsyncs instead of running per-address in parallel, roughly 0.1–10 ms of global serialization per append at the default `FlushToDisk = true`. That cost no longer scales with writer count at all — 8 and 16 concurrent writers reach the same append-per-second ceiling, because every writer now serializes behind the same global gate regardless of how many addresses they touch. That is the accepted price of making the change-log line the commit point, which is the only channel this provider has to a reader in another process. The change-log append is fsynced at `FlushToDisk = true`, so the globally serialized portion of an append is two fsyncs rather than one. Measured at 7.6 ms/append, against 7.2 ms/append before that second fsync was added.

Retention costs the same thing on every provider now, and the rule is one sentence: **a record leaves the change feed exactly when its history record leaves the store; retention that removes a revision removes that revision's feed entry, on every provider.** Redis removes the pruned members from the `:changes` sorted set in the same transaction that removes them from the per-address history sorted set, by member rather than by score range. The in-memory provider removes the pruned positions from its feed under the same lock its appends publish under. Entity Framework Core needs no separate step, because one table serves history and feed. The filesystem provider deletes the history file and leaves the change-log line behind as a dangling entry that `ReadAsync` skips: the record is gone from the feed, but the log file still holds the line, so this provider's feed *storage* still grows with total writes ever made — compaction is a separate, unshipped item. `StateRetentionPolicy.KeepAll` is the default with every bound null, so a store that has not configured a bound loses nothing to any of this; the change is visible only to a deployment that asked for `MaxRevisions` or `MaxBytes` and, on Redis and the in-memory provider, was not getting it.

`ReadAsync` takes a `StateChangeReadOptions` whose `Take` bounds how many records one read
yields; `null` (the default, and `StateChangeReadOptions.Default`) reads to the tail. Every
provider honours it natively rather than filtering after the fact: in-memory and Entity
Framework Core through `Take` on the query — server-side `LIMIT` on Entity Framework Core —
Redis through `ZRANGEBYSCORE … LIMIT`, and the tiered store by forwarding to cold. `Take`
bounds records **yielded**, which matters on the filesystem provider: a pruned record leaves a
dangling change-log line, and the scan continues past it rather than counting it against the
page, because a page of nothing but dangling entries would otherwise look like the end of the
feed. The filesystem provider is also the one place `Take` does not bound the work, though it no
longer costs what it did: the provider keeps an in-process index of its change log, so the
whole-log parse is paid once per store instance and then only for the bytes appended since
the previous read. A paging drain is linear in backlog size again — measured at 121.16 ms for
the first read and 0.04 ms for a subsequent one at 200,000 change-log lines, against 78.76 ms
per read before the index, and a 20,000-record backlog drains in 1,100 ms against 3,256 ms
before — roughly 1,100 ms of both drain figures is the drain's 20,000 history-file reads, one
per yielded record, which the index does not touch, so the log-scanning portion of the drain
fell from about 2,150 ms to near zero. (Figures taken on the development machine with
`STATESMAN_MEASURE_FEED_SCAN=1`; they are a shape, not a contract.) What `Take` still bounds
is the expensive part, one history-file read per yielded record. The index costs resident
memory for the store's lifetime: an entry is two `long`s plus a `StateAddress`, which is three
string references, so about 40 bytes per change-log line plus one shared set of strings per
distinct address — roughly 8 MB at 200,000 lines. It survives an append by
another process — the next read parses the new suffix — and it is discarded and rebuilt if
the log shrinks or if its remembered tail bytes no longer match, which is what makes a
truncation or a future compaction safe. It is per process: a second process reading the same
directory pays its own first parse.
A page shorter than `Take` means the provider reached its tail as of that read; because a write
in flight holds back later records, it does not prove the feed is exhausted, so a paging
consumer resumes from the last cursor it received and reads again rather than concluding it is
caught up.

`StateChangeCursor` carries no store identity. Passing a cursor obtained from one store into a different store's `ReadAsync` is not rejected — it silently returns whatever slice that position happens to mean for the second store.

## Change notification semantics and limitations

The in-memory, Redis, and tiered providers implement `IStateChangeNotifier`, a low-latency hint that a store's change feed may have advanced. The filesystem and Entity Framework Core providers do not, and consumers over those poll.

The guarantee is deliberately small and identical on every provider that implements it: a notification is a signal to poll `IStateChangeFeed` now. It carries no payload, no cursor and no delivery guarantee, and it can arrive for a record the feed will not yet yield because a lower position is still in flight. A consumer that polls on a hint and sees nothing must poll again, and must never advance its cursor from a hint. Losing every hint costs latency and nothing else — the feed is what makes a record recoverable, and it is lossless within retention on every provider. Hints may also be coalesced: a burst of writes can produce a single hint, by design. The subscription is established when enumeration starts rather than when `SubscribeAsync` is called, so a hint for a write that lands before the first `MoveNextAsync` is missed by design; disposing the enumerator is what unsubscribes.

**In memory.** One bounded capacity-1 channel per subscriber in drop-write mode: a hint raised while a subscriber's channel is full is discarded, so a burst of appends collapses to at most one extra consumer cycle and a slow or idle subscriber can never apply backpressure to a writer. Hints are raised after the append's feed lock is released, so a later record can occasionally be hinted before an earlier one — both hints say the same thing, and the feed's order is unaffected. Disposing the store ends every live subscription.

**Redis.** `PUBLISH` to `{KeyPrefix}:{name}:notifications` after the append script returns and after an import's transaction commits, as a fire-and-forget command that adds no round trip to an append. Redis pub/sub is at-most-once with no backlog: everything published while a subscriber is disconnected is lost forever and nothing is replayed on reconnect. Because the publish happens client-side after the record is already on the change-feed sorted set, a Redis hint never arrives before the feed can yield the record it refers to — the contract still permits that, and other providers may do it, so a portable consumer must not rely on it. A subscription that is never disposed holds a server-side pub/sub subscription for the lifetime of the connection.

**Filesystem.** Not implemented, and that is a deliberate "No" rather than a gap. This provider's change log is its only channel to a reader in another process, and no reliable cross-process notification primitive matches it: `FileSystemWatcher`'s buffer can overflow and lose track of changes, a single file operation can raise more than one event, and network directories cap the buffer and depend on platform support. An in-process-only notifier would claim a capability that dies at the process boundary the change log exists to cross. Consumers poll, on the provider whose append latency is already dominated by an fsync.

**Entity Framework Core.** Not implemented. The package references only EF Core and EF Core Relational and is provider-neutral by design; every push mechanism is engine-specific and SQLite has none. `OutboxOptions.PollInterval` is the same mechanism under an honest name.

**Tiered.** Implemented directly and delegated to the cold tier, matching `IStateChangeFeed` and `IPartitionCatalog`. Hints describe cold's feed, which is the feed a tiered store's `ReadAsync` yields and whose positions its cursors carry. A hot-tier hint is never used, even when the hot store has a notifier and the cold store does not: in that case discovery still succeeds and `SubscribeAsync` throws `NotSupportedException` naming the cold tier.

## Partition catalog semantics and limitations

The filesystem provider's `IPartitionCatalog` implementation is not atomic with the record write in one respect: `AppendAsync` and `ImportAsync` write the record's history file and then append a line to the change-feed log file that `ListPartitionsAsync` reads from, both inside one critical section, and write the head file afterwards outside it. A crash or I/O failure in the window between the history write and the change-log append leaves a durably-committed partition invisible to `ListPartitionsAsync` until the next successful write to that same address re-adds it — the catalog is not permanently wrong, just transiently stale after a crash, and it self-heals on the next write to that address. The in-memory provider's single lock, Entity Framework Core's single transaction, and Redis's single append script (or, for import and distributed capture, its single `MULTI`/`EXEC`) each cover the record and the catalog update together, so none of them has an equivalent window.

The filesystem catalog can also report a partition that was never committed. A torn final line in the change log — a crash mid-append, or a cross-process reader catching one in flight — is skipped when it fails to parse, but one that happens to keep five tab-separated fields with parseable numbers parses cleanly and yields a partition descriptor for an address the store never wrote. `IStateChangeFeed.ReadAsync` filters that phantom out because the record's history file is missing; `ListPartitionsAsync` has no such filter and lists it. Pre-existing, unchanged by the change-log length guard, and self-correcting once the partial line is truncated or the address is genuinely written.

The filesystem provider's `ListPartitionsAsync` is also O(total writes ever made to the store), not O(partition count): it scans the entire change-feed log on every call. It holds the same gate that `AppendAsync` and `ImportAsync` use for the whole duration of that scan, which blocks concurrent appends outright while a listing is in progress. `IStateChangeFeed.ReadAsync` on this provider no longer shares that shape: it keeps an in-process index and parses only the appended suffix, while the partition catalog still parses the whole log under the gate on every call. Making the catalog incremental is a separate, unshipped item.

## Distributed capture semantics and limitations

Redis and Entity Framework Core implement `IDistributedCapture`, giving callers a coherent, multi-address snapshot read for both `StateCaptureConsistency.ReadCommittedDistributed` and `StateCaptureConsistency.SnapshotDistributed`.

Redis backs both levels with an unconditional `MULTI`/`EXEC` batch of head reads. Standalone Redis executes commands single-threaded, so this is a true, exact snapshot for either requested level — there is no weaker mechanism to fall back to. Against Redis Cluster, addresses whose keys span more than one hash slot cannot share one `MULTI`/`EXEC` transaction; a `CROSSSLOT` failure — raised client-side by StackExchange.Redis before dispatch, or server-side on the transaction itself, and observable either on `ExecuteAsync` or later on an individual queued read — is translated into `NotSupportedException` rather than silently degrading to unsynchronized sequential reads. Both the scenario and the exception types caught (`RedisCommandException`, `RedisServerException`) are reasoned rather than verified: reproducing a `CROSSSLOT` failure needs a real Redis Cluster deployment, which this repo's test infrastructure (`STATESMAN_TEST_REDIS`, standalone only) does not provide.

Entity Framework Core maps the two levels onto genuinely different database isolation levels — `ReadCommittedDistributed` to `IsolationLevel.ReadCommitted`, and `SnapshotDistributed` per provider: `IsolationLevel.Snapshot` on SQL Server, `RepeatableRead` on PostgreSQL, which has no `Snapshot` level, and `Serializable` on SQLite and any other provider. That is the codebase's only provider-conditional branch and it exists because `Serializable` is not a snapshot on SQL Server: it is lock-based, a key-range lock taken for one address need not cover another, and a capture could therefore return one address's pre-write revision beside another's post-write revision. `Snapshot` requires `ALTER DATABASE … SET ALLOW_SNAPSHOT_ISOLATION ON` to have been run before any connection opens such a transaction; a database without it fails at `BeginTransaction` rather than reading at the wrong level. On SQLite `Serializable` still opens the transaction as `BEGIN IMMEDIATE`, which takes a database-wide write-intent lock the instant the transaction starts and holds it until commit, so a `SnapshotDistributed` capture of N addresses blocks every other writer in the database for N round trips; operators should treat a wide `SnapshotDistributed` capture against SQLite as a latency and availability trade-off.

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

One residue of that colliding case is worth naming, because retention does not collect it: re-importing an existing revision at a *different* `GlobalPosition` leaves the earlier feed entry behind on three providers — Redis keeps a `:changes` member with no history twin (import replaces history by revision score and changes by position score), the in-memory provider keeps the earlier entry, and the filesystem provider's earlier log line now dereferences to the rewritten history file, so that record yields at two positions. It is pre-existing, it only arises in the colliding-lineage restore this paragraph describes, and it is documented rather than fixed.

## Outbox delivery semantics and limitations

`Statesman.Outbox` reads a store's `IStateChangeFeed` from a persisted cursor and publishes to an `IStateChangeSink`, composing that capability's own guarantees rather than adding new ones — see the [Outbox delivery](../guides/outbox.md) guide for the operator view. The composed promise, stated identically there and on `StateChangeDispatcher`'s XML doc: every record the outbox reads from the feed is handed to the sink at least once, and the persisted cursor never advances past a record the sink has not accepted.

Per-provider suitability as an outbox source follows directly from "Change feed semantics and limitations" above. Any provider with `StateRetentionPolicy.KeepAll` is a complete configuration: the feed is lossless within retention on all five, so a `KeepAll` store loses nothing to either caveat. The filesystem provider carries one residual — a crash between the record write and the change-log append leaves that record permanently absent from the feed — so a deployment that must not lose a record across a host crash should prefer Entity Framework Core or Redis. That window is now the only filesystem feed residual: both writes are fsynced at `FlushToDisk = true`, so a host or power failure no longer loses log lines whose history files survived. Any provider with a configured (non-`KeepAll`) retention policy inherits prune loss on **all five**, because `StateHandle` prunes after every successful append and a record leaves the feed exactly when its history record leaves the store. The providers differ only in what is left behind: the filesystem provider keeps a dangling change-log line whose record is gone, which `ReadAsync` skips and which a future compactor will remove, so its log file keeps growing; the other four leave nothing. The tiered provider reads its change feed from its cold tier and takes its outbox lease hot-first, so an outbox over a tiered store must key its cursor by the tiered store's own name, not its cold store's — the two are different `IStateLedgerStore.Name` values with unrelated cursor positions.

Only Redis and Entity Framework Core implement `IStateLeaseProvider`, which is what the outbox's default `RequireLease = true` requires to keep one dispatcher at a time advancing the cursor. An outbox over the filesystem or in-memory provider needs `RequireLease = false`, accepting that a second dispatcher can advance the cursor past records neither of them published.

The Redis stream sink's entry-id contract is `{globalPosition}-0`. This is exact for every `long` position, because a Redis stream entry id is two unsigned 64-bit integers, and it is explicitly **not** subject to `RedisStateLedgerStore.MaxImportablePosition` (2^52), which bounds the ledger's own sorted-set change-feed scores, not stream entry ids — a filesystem export's UTC-tick positions, too large to restore into Redis's ledger, publish to a Redis outbox stream without truncation. `GlobalPosition` is unique only within one store's own position lineage, so a stream key must be exclusive to one store: the sink verifies a rejected `XADD` is genuinely this message republished (by comparing the existing entry's `messageId`) before counting it as a duplicate, and throws rather than silently discarding a message when the position instead belongs to a different store's lineage sharing that key.

There is a known `MessageId` collision case, documented rather than fixed: `StateAddress` equality is ordinal on `Root` while `Canonical` lower-cases it, so a store written under both `"App"` and `"app"` has one revision sequence on the in-memory and filesystem providers (whose per-address gate keys on the canonical address) but two independent revision sequences on Entity Framework Core (which keys on the exact root). The outbox's `MessageId` is built from the canonical address, so those two Entity Framework Core sequences can produce colliding message ids.

Entity Framework Core cursor storage ships in `Statesman.Outbox.EntityFrameworkCore`, which carries its own `StatesmanOutboxCursorDbContext` rather than adding a `DbSet` to `StatesmanLedgerDbContext` — so an existing ledger consumer gains no migration, and a consumer who wants Entity Framework Core cursors adds one migration for one independent table. The monotonic write is a single `UPDATE … WHERE OutboxId = @id AND Position < @new` through `ExecuteUpdate`, so the comparison and the assignment are one statement: on SQL Server the row lock makes them atomic, and on SQLite the statement serializes at database level, which is stronger. Both Entity Framework Core packages now run against live SQL Server and PostgreSQL instances in CI as well as SQLite, so the row-lock path is observed rather than reasoned about, and the cross-address append race the handover previously documented is fixed. Two engine limitations remain, both pinned by gated tests. SQL Server's 900-byte clustered index key limit applies to the shipped composite key: `CREATE TABLE` succeeds with a warning and an insert whose actual key exceeds 900 bytes fails at runtime with `Msg 1946`, surfaced as `DbUpdateException`. And PostgreSQL stores `timestamptz` to the microsecond while a .NET tick is 100 ns, so `OccurredAt`, `FreshUntil`, `ServeUntil` and the lease table's `ExpiresAt` lose their last tick digit on that engine; nothing in this library compares those values at finer than microsecond resolution, and every provider re-reads a lease row before writing it, so the truncation is visible only to a caller that stores a sub-microsecond timestamp and expects it back exactly. One consequence of the append's internal retry is worth knowing when writing retry logic of your own: if a commit succeeds on the server but its acknowledgement is lost, the retry re-reads the head, finds the record it just wrote, and returns `Conflict` carrying that record. Nothing is duplicated and no position is reused, but a caller that reads `Conflict` as "another writer won" should compare the returned record with the one it intended to write. One configuration is not supported: a consumer who calls `EnableRetryOnFailure` gets a retrying execution strategy, and every method on this store opens its own transaction, so all of them throw `InvalidOperationException` naming `DbContext.Database.CreateExecutionStrategy()`. Leave retry-on-failure off; the store already retries a lost position allocation itself.
