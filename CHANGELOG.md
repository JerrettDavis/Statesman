# Changelog

All notable changes to Statesman are documented here. The project follows Semantic Versioning once the public API reaches 1.0. Preview releases may refine APIs when doing so materially improves the authority or consistency model.

## [Unreleased]

### Added

- declaration-driven state runtime and deterministic manifest fingerprint
- typed keys, partitions, attached and isolated containers, and multiple registered roots
- composed proactive loaders with sequential or parallel fetch and require-all or best-effort failure policies
- reactive writes, typed interactions, requirements, reducers, invariants, and schema migrations
- immutable snapshots, explicit read modes, freshness windows, stale-while-revalidate, and last-known-value fault behavior
- append-only ledger operations, optimistic revisions, retention, history, local capture, and observation
- in-memory, filesystem, Redis, Entity Framework Core, and tiered hot/cold providers
- dependency injection, hosting, HTTP, ASP.NET Core, analyzer, and testing packages
- fluent `StateSeedBuilder` scenario setup and portable `StateFixture` capture/load/apply workflows
- untyped runtime mutation operations for dynamic test/tooling orchestration without bypassing state rules
- console, ASP.NET Core, migration, multi-root, and repeatable testing/fixture samples
- unit, provider, analyzer, dependency-injection, and end-to-end test projects
- cross-platform CI, CodeQL, package, release, and dependency-update workflows
- `IStateCapability` capability-negotiation convention (`TryGetCapability<T>`) with
  `IStateCapabilityProvider` forwarding for wrapping stores such as the tiered provider
- `IStateLeaseProvider`/`IStateLease` distributed lease capability, implemented by the Redis and
  Entity Framework Core providers; `StatesmanRuntime` maintenance now coordinates interval refresh
  per store through a lease when the backing store supports one. Existing Entity Framework Core
  consumers must add and apply a migration for the new `StatesmanLeases` table before upgrading —
  `StatesmanRuntime.MaintainAsync` now calls it unconditionally for any EF-backed store, and
  maintenance will log-and-skip (not crash) that store on every tick until the table exists
- `IStateChangeFeed` durable, cursor-resumable cross-stream change feed capability, implemented natively by all five providers (EF Core over its existing indexed column; Redis via a new global sorted set; filesystem via a new append-only index; in-memory via a queue; tiered delegating to cold) — see `docs/providers/index.md`'s "Change feed semantics and limitations" for the at-least-once/tail-loss caveat under concurrent writes and other per-provider trade-offs before building on it
- `IPartitionCatalog` partition-discovery capability, implemented natively by all five providers (in-memory reusing its existing `_streams` map; EF Core enumerating `StatesmanHeads`; Redis via a new hash written in the same `MULTI`/`EXEC` as the change-feed entry; filesystem reusing the change-feed log; tiered delegating to cold) — see `docs/providers/index.md`'s "Partition catalog semantics and limitations" for the filesystem provider's post-crash staleness window and its O(total writes) listing cost
- `IDistributedCapture` coherent multi-address capture capability, implemented by the Entity Framework Core provider (a transaction whose isolation level is mapped from the requested consistency) and the Redis provider (a `MULTI`/`EXEC` batch of head reads), with tiered delegating to cold; the filesystem and in-memory providers do not implement it. `IStatesman.CaptureAsync` and `IStateContainer.CaptureAsync` gain a `StateCaptureConsistency required` parameter defaulting to `ProcessLocal`, so existing call sites keep today's behavior unchanged — see `docs/providers/index.md`'s "Distributed capture semantics and limitations" for the per-store scope of the snapshot guarantee, Redis Cluster's `CROSSSLOT` limit, and SQLite's write-stall trade-off under `SnapshotDistributed`
- `IReplicationLagSource` replication-lag capability, implemented only by the tiered provider, which compares its hot replica against its cold authority through both tiers' `IPartitionCatalog` and reports the newest head position of each tier plus the number of partitions the replica lacks or holds stale as a `StateReplicationLag`; the estimate reads hot before cold so it is designed to over-report rather than under-report lag under concurrent writes — see `docs/providers/index.md`'s "Replication lag semantics and limitations" for why `PositionGap` is a distance in a sparse sequence rather than a record count and why lazily-populated partitions count as behind
- `IStateLedgerReplica` on the Redis provider: an exact, idempotent import that writes head, revision guard, history, change feed, and partition hash in one transaction and raises the global position counter to at least the imported position first, so Redis can serve as a tiered hot replica or a restore target; positions above 2^52 are refused because the change feed's sorted-set scores are IEEE doubles, so a filesystem export (UTC-tick positions) cannot be restored into Redis
- `Statesman.Tooling` package: `StateLedgerExport` writes one root's retained ledger history (via `IPartitionCatalog` plus per-partition history, not the change feed) to a newline-delimited JSON file stamped with the declaration fingerprint and format version; `StateLedgerRestore` validates the whole file against the target's manifest before importing anything, refuses on any mismatch or truncation, requires `IStateLedgerReplica`, and refuses a non-empty target by default — see `docs/guides/backup-restore.md`
- `Statesman.Outbox` and `Statesman.Outbox.Redis` packages: a lease-gated outbox that reads a store's `IStateChangeFeed` from a persisted, monotonically-advancing cursor and publishes each record to an `IStateChangeSink` as a `statesman.state-change/v1` message, at least once, keyed for deduplication by `{store}/{address}#{revision}`. `OutboxOptions.RequireLease` defaults to `true`, so a store without `IStateLeaseProvider` is refused by name rather than silently dispatching from two processes; the worker is the repository's first production caller of `IStateLease.RenewAsync`. The Redis bridge publishes to a stream with the explicit entry id `{globalPosition}-0`, which makes a re-publish after a crash a server-side no-op. Entity Framework Core cursor storage is deliberately not shipped, so no existing consumer needs a second migration — see `docs/guides/outbox.md`'s "What \"delivered\" means" for the feed's tail-loss and prune caveats, which the outbox inherits and cannot fix, and for the one complete configuration (Entity Framework Core with `StateRetentionPolicy.KeepAll`)
- `IStateChangeFeed` is now lossless within retention on every provider: a record's `GlobalPosition` is allocated in the same step that makes the record visible to the feed, so a consumer resuming from the cursor of the last record it accepted is never skipped past a record. The in-memory provider allocates and publishes under one lock; the filesystem provider allocates, writes the history file, and appends its change-log line under one global gate (writing the head file outside it), which globally serializes one `WriteThrough` fsync per append and is a real append-throughput change operators will measure; Redis moves `INCR` and all five writes into one Lua script, which also means a rejected conditional append no longer burns a position, so Redis positions are dense. Redis stores `globalPosition` as the last JSON property of a record — a write-order change only, with deserialization unaffected, so no stored data needs migrating. Entity Framework Core already allocated at commit time and is unchanged, with a new regression guard pinning the `StatesmanSequences` row mechanism that makes it safe. Retention is now the only remaining feed caveat, plus a filesystem crash window between the record write and the change-log append — see `docs/providers/index.md`'s "Change feed semantics and limitations"

### Fixed

- assign `net10.0` explicitly to `Statesman.Sample.Testing`, which previously had no effective target framework
- chain `tests/Directory.Build.props` to the repository build props so test projects inherit implicit usings, nullable analysis, language settings, and warning policy
- make Nerdbank.GitVersioning conditional on Git metadata so downloaded source archives can restore and build outside a Git checkout
- evaluate package README inclusion from `Directory.Build.targets`, after each project has declared `IsPackable`
- harden analyzer nullable flow and analyzer-test metadata reference construction
- replace invalid nullable `TimeSpan` relational patterns with explicit value comparisons
- extend offline validation to catch missing target frameworks, nested `Directory.Build.props` shadowing, and missing solution configuration mappings
- close a race in the Entity Framework Core provider's `IStateLeaseProvider.AcquireAsync` where two concurrent first-acquisitions of a never-before-seen lease could surface an uncaught `DbUpdateException` instead of the documented "someone else got it" `null` return; it now mirrors `AppendAsync`'s catch/rollback/re-read pattern and rethrows only when the re-read shows the lease is not actually held
- stop the tiered provider from forwarding `IStateLedgerReplica` discovery to its hot replica: the hot store is the tiered store's private cache-repair channel and the cold store is authoritative, so `TryGetCapability<IStateLedgerReplica>` on a tiered store now returns false instead of handing callers (such as a `Statesman.Tooling` restore) a way to import exact records into the cache that the authority never sees — restore into the cold store directly

### Notes

This archive was assembled in an environment without a .NET SDK. The offline structural validator is included and its report is packaged, but compilation and execution must be performed by the included build scripts or CI.
