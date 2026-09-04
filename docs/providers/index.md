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
