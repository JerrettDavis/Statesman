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
