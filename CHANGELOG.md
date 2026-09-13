# Changelog

All notable changes to Statesman are documented here. The project follows Semantic Versioning once the public API reaches 1.0. Preview releases may refine APIs when doing so materially improves the authority or consistency model.

## [Unreleased]

### Breaking

- **`IStateChangeFeed.ReadAsync` now takes a `StateChangeReadOptions`** between the cursor and the
  cancellation token: `ReadAsync(StateChangeCursor? from, StateChangeReadOptions options, CancellationToken cancellationToken = default)`.
  Pass `StateChangeReadOptions.Default` to read to the feed's tail, which is the previous
  behaviour exactly. `StateChangeReadOptions.Take` bounds how many records one read yields —
  `null`, the default, is unbounded, deliberately not the 100 that `StateHistoryOptions.Take`
  defaults to — and every provider honours it natively: server-side `LIMIT` on Entity Framework
  Core, `ZRANGEBYSCORE … LIMIT` on Redis, `Take` on the in-memory query, a bounded yield on the
  filesystem provider, and forwarding on the tiered store. A page shorter than `Take` does not
  prove the feed is exhausted. The change is breaking rather than additive because a defaulted
  parameter cannot be inserted ahead of `CancellationToken` without silently rebinding positional
  callers, and because an unbounded overload left in place would keep the ambiguity the parameter
  exists to remove. `Statesman.Outbox` now pages the feed at `OutboxOptions.BatchSize`, so that
  option bounds what the feed materializes as well as what is published on Redis, Entity Framework
  Core and the in-memory provider — including the filesystem provider, whose change log is now
  read incrementally (see the `### Fixed` entry below) — and gains no companion.
- **`Statesman.Outbox.OutboxCursorFile` is now `internal`.** The `public sealed record
  OutboxCursorFile(string OutboxId, long Position)` in `FileSystemOutboxCursorStore.cs` describes
  only the on-disk shape of a cursor file, and has had no consumer anywhere in `src` or `tests`
  since it shipped in `0.3.0`. The change is breaking rather than additive because the type is
  public API today; it is taken deliberately in the same `0.4.0-alpha` window as the
  `IStateChangeFeed.ReadAsync` break above, on the reasoning that a break not taken in the same
  release as the public-API baseline gate (see `### Added`, below) needs a second suppression
  entry written later, against a baseline that by then still records the type as public. Recorded
  in `src/Statesman.Outbox/CompatibilitySuppressions.xml` and explained on the new
  `docs/reference/api-compatibility.md`.

### Added

- `Statesman.Outbox.EntityFrameworkCore`, a new package holding an `IOutboxCursorStore` backed by
  Entity Framework Core. It ships its own `StatesmanOutboxCursorDbContext` with a single
  `StatesmanOutboxCursors` table and does **not** reference
  `Statesman.Persistence.EntityFrameworkCore`, so no existing Entity Framework Core ledger
  consumer gains a migration — only a consumer who opts into Entity Framework Core cursor storage
  adds one, for one independent table. Register with
  `AddStatesmanEntityFrameworkOutbox<TContext>(configure, sink)`, or supply
  `UseEntityFrameworkCursors<TContext>()` as `AddStatesmanOutbox`'s `cursors` argument. The
  monotonic write is one conditional `UPDATE … WHERE Position < @new` through `ExecuteUpdate`
  rather than a transaction or a concurrency-token retry loop. Targets .NET 10 only, as the
  Entity Framework Core ledger provider does.
- **`FileSystemStateLedgerStore.CompactChangeLogAsync`**, with a `dryRun` overload and a
  `ChangeLogCompactionResult` carrying `LinesBefore`, `LinesAfter`, `BytesBefore`, `BytesAfter` and
  `BytesReclaimed`. It rewrites `_changes.log` without the lines that no longer dereference to a
  stored record — the dangling line a prune leaves behind, and the line an import left pointing at a
  revision that has since moved to a different `GlobalPosition`. An unterminated final line is copied
  through untouched and no two byte-identical lines are ever emitted. **This is a storage and
  scan-cost fix, not a correctness fix**: a dangling entry was already skipped on read. It is not
  called for you and there is no auto-compact option — `StateHandle` prunes after every successful
  append, and compacting there would make an unrelated address's append wait out a whole-file
  rewrite. Call it on a maintenance cadence, in the writer's own process: the provider is
  single-writer by design, and an appender takes two file opens, so a compactor elsewhere could
  rename the log between a writer's tail check and its append. The store directory gains one small
  sidecar file, `_changes.gen`, holding a compaction generation; a missing one reads as zero, so no
  existing directory needs migrating.
- **`ManualTimeProvider.CreateTimer`** — see `### Changed`, because it replaces inherited behaviour
  rather than adding absent behaviour.
- Both Entity Framework Core packages now run their full test suites against live SQL Server and
  PostgreSQL instances in CI, alongside SQLite.
- **`Statesman.Persistence.EntityFrameworkCore.{Sqlite,SqlServer,PostgreSQL}` and
  `Statesman.Outbox.EntityFrameworkCore.{Sqlite,SqlServer,PostgreSQL}`**, six new packages — three
  per context, one per engine — each shipping one `Initial` migration for `StatesmanLedgerDbContext`
  or `StatesmanOutboxCursorDbContext`. Each references exactly its own core package plus exactly one
  provider package and never the sibling context's core package, so a ledger-only consumer still
  never pulls in the outbox core package (or the reverse); each also adds
  `Microsoft.EntityFrameworkCore.Design` as `PrivateAssets="all"`, so the design-time assembly never
  reaches a consumer's dependency closure. Register with
  `options.UseSqlite(connectionString).UseStatesmanLedgerSqliteMigrations()` (or the matching
  `SqlServer`/`PostgreSql` and outbox extension methods): the call re-adds the already-configured
  `RelationalOptionsExtension` with the shipped migrations assembly and one of two dedicated history
  tables — `__StatesmanLedgerMigrationsHistory` or `__StatesmanOutboxMigrationsHistory` — and
  replaces `IMigrationsAssembly` so a consumer's subclass of either context discovers the shipped
  migration too. Targets .NET 10 only, like the two core packages. Shipped migrations remain opt-in
  and consumer-owns-the-migration stays the documented default; a shipped migration id is a permanent
  public contract, never renamed, removed, or regenerated.
- Two runtime meter counters, `statesman.maintenance.failures` and
  `statesman.maintenance.failures.dropped`, on the existing `Statesman` meter. The runtime now
  retains only the most recent 64 maintenance-failure exceptions — previously unbounded — and the
  counters report how many were recorded and how many were dropped once that bound is exceeded, so
  an application can surface the retention through its normal metrics infrastructure even though
  there is still no API to read or clear the retained collection itself.
- **A public, rate-limited maintenance-failure diagnostics surface**: `IStatesmanDiagnostics`
  (`ReadMaintenanceFailures`, `ClearMaintenanceFailures`), discovered on any `IStatesman` with the
  new `StatesmanDiagnosticsExtensions.TryGetDiagnostics`, and the new `MaintenanceFailure`,
  `MaintenanceFailureDiagnostics`, and `StatesmanDiagnostics` types in `Statesman.Abstractions`. An
  application can now read the retained failures — each with its store name, timestamp, and
  exception — instead of only their counts, and clear them after draining. Retention is also now
  rate-limited per ledger store, at `StatesmanDiagnostics.MaintenanceFailureRate` failures per
  `StatesmanDiagnostics.MaintenanceFailureRateWindow`, ahead of the existing
  `StatesmanDiagnostics.MaxRetainedMaintenanceFailures` retention bound (now public rather than a
  private literal), so a single store whose maintenance keeps failing can no longer crowd out every
  other store's failures in the retained set. A third meter counter,
  `statesman.maintenance.failures.suppressed`, counts what the rate limit discarded, alongside the
  existing `statesman.maintenance.failures` and `statesman.maintenance.failures.dropped`.
- **A public-API compatibility baseline gate — the last unshipped ROADMAP 0.2 bullet.**
  `PackageValidationBaselineVersion` is set to `0.3.0` in `src/Directory.Build.props` for the
  fifteen packages `v0.3.0` published, so `dotnet pack` now fails on a future accidental public-API
  break. The seven packages with no `0.3.0` release (`Statesman.Outbox.EntityFrameworkCore` and the
  six Phase 14 migration packages) blank the property in their own project file. Documented on the
  new `docs/reference/api-compatibility.md`, which every `CompatibilitySuppressions.xml` entry
  cross-references; see the `### Breaking` entry above for the one deliberate break this gate
  records against `0.3.0` beyond `IStateChangeFeed.ReadAsync`.
- The outbox hosted service now logs once when a replica enters standby (cannot take its lease) and
  once when it leaves, instead of never. Previously a deployment where every replica was standby,
  or where takeover silently never happened, produced no log line at all; a steady standby state
  still logs nothing further, and an unrelated exception on an intervening cycle does not swallow
  the eventual exit log.
- `STATESMAN_MEASURE_OUTBOX_CURSOR_WRITE`, an optional environment variable gating a measurement
  harness for `FileSystemOutboxCursorStore`'s write path, joining `STATESMAN_MEASURE_FEED_SCAN` and
  `STATESMAN_MEASURE_INMEMORY_DRAIN`.
- **`StatesmanHealthCheck`** (`Statesman.Extensions.Hosting`), an `IHealthCheck` over every
  registered runtime: unhealthy while a root has not finished `InitializeAsync`, degraded when
  maintenance failures are retained or a store is running interval maintenance without a lease, and
  healthy otherwise. Its `HealthCheckResult.Data` carries the same `statesman.` keys the loader
  metadata and the meter already use. `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` is
  the only new dependency — measured during planning not to move the public-API baseline gate.
  `IHealthChecksBuilder` and `AddCheck<T>` live in the larger, non-abstractions health-checks package
  (measured against `10.0.11`), so there is no `AddStatesman` registration extension; register with
  `services.AddHealthChecks().AddCheck<StatesmanHealthCheck>("statesman")` instead — a larger
  dependency for one extension method was not worth it.
- **A telemetry reference page and a pinning test**: `docs/reference/telemetry.md` is the
  OpenTelemetry semantic-convention audit for `StatesmanTelemetry`'s meter — every instrument's name,
  kind, unit, and meaning; the four tag keys; and the two places the shipped names deviate from the
  conventions (`statesman.operation.duration` records milliseconds where the conventions ask for
  seconds with unit `s`; the counters carry no annotation unit such as `{fault}`), kept through the
  `0.x` line because changing either would shift or rename an already-shipped instrument's identity
  for no behavioural gain, with a migration note for when `1.0` corrects them.
  `StatesmanTelemetryConventionTests` pins every fact this page states with a set-equality check, so
  an instrument added, renamed, or retyped without a decision fails a test instead of drifting from
  the page silently.

### Changed

- **Maintenance-failure retention is now rate-limited ahead of the existing 64-entry bound.** Each
  ledger store gets a token bucket of `StatesmanDiagnostics.MaintenanceFailureRate` (16) failures per
  `StatesmanDiagnostics.MaintenanceFailureRateWindow` (one minute); a failure the bucket refuses is
  counted on the new `statesman.maintenance.failures.suppressed` counter and never reaches retention
  at all. Previously every reported failure was retained until the 64-entry bound evicted the oldest.
  **An operator watching `statesman.maintenance.failures.dropped` will see it go quiet**: a single
  source failing continuously now stalls at 16 suppressed failures per window well before it can ever
  push the retained queue past 64, so `dropped` only still increments when several distinct stores are
  failing inside the same window. Acceptance is unaffected — an append is authoritative whether or not
  its prune failed, rate limit or no.
- **`ManualTimeProvider` now overrides `GetTimestamp()` and `TimestampFrequency`**, so
  `TimeProvider.GetElapsedTime` measures elapsed *virtual* time against it. Previously both fell
  through to the base implementation, which returns `Stopwatch.GetTimestamp()` at
  `Stopwatch.Frequency` — so a caller measuring an interval with timestamps rather than with a timer
  silently ran on the system clock and ignored `Advance`. This is a behaviour change for existing
  callers, in the same category as the `CreateTimer` override that shipped in 0.3's previous phase.
  Both members are overridden together because the base `GetElapsedTime` scales a raw delta by the
  reported frequency; overriding only the timestamp would read correctly on Windows and 100 times
  short on Linux.
- **`StateChangeDispatcher` measures lease-renewal cadence with a `TimeProvider`** instead of
  `Stopwatch`, through a new five-argument constructor overload that takes one. The four-argument
  constructor is unchanged and delegates to it with `TimeProvider.System`, which is the same counter
  at the same frequency, so no shipped behaviour changes. `AddStatesmanOutbox` passes whichever
  `TimeProvider` the container holds, so a host that registers a test clock now has one that reaches
  renewal as well as the poll timer.
- **Retention now trims the change feed on every provider.** The rule: a record leaves the
  change feed exactly when its history record leaves the store, so
  `StateRetentionPolicy.MaxRevisions` and `MaxBytes` bound a provider's feed as well as its
  per-address history. Two providers change: `RedisStateLedgerStore.PruneAsync` now removes
  the pruned members from the `{prefix}:{name}:changes` sorted set in the same transaction
  that removes them from the per-address history set (member-exact `ZREM`, never
  `ZREMRANGEBYSCORE`, whose bounds are IEEE doubles), and `InMemoryStateLedgerStore` now holds
  its feed in a position-ordered set that `PruneAsync` removes from under the same lock its
  appends publish under. Entity Framework Core and the filesystem provider already behaved
  this way and are unchanged. **This turns a documented no-prune-loss property on Redis and
  the in-memory provider into prune loss**, which is the memory bound those two options
  always promised: before this, a store configured with `MaxRevisions` grew its feed without
  limit. `StateRetentionPolicy.KeepAll` is the default with every bound null, so a
  default-configured store loses nothing. Cursor semantics are unchanged — a cursor at a
  position retention removed still resumes at the next surviving record. The filesystem
  provider still leaves a dangling change-log line per pruned record, which `ReadAsync` skips
  and which log compaction (not in this release) would remove.

- `IStateLease.RenewAsync` now defines "lost": a lease is lost once its time-to-live has lapsed
  or another holder has acquired it, and renewal returns `false` in both cases without ever
  resurrecting an expired lease. Redis already behaved this way; the Entity Framework Core
  provider changes to match — it now checks `ExpiresAt` against its injected `TimeProvider` and
  reports a lease lost to a concurrent re-acquisition as `false` instead of throwing
  `DbUpdateConcurrencyException`. A caller that previously relied on renewing an Entity
  Framework Core lease after its TTL lapsed must acquire again instead.
- the outbox dispatcher is now a **persistent leader**: it holds its `IStateLease` across
  dispatch cycles and renews it on `OutboxOptions.LeaseRenewInterval` instead of acquiring and
  releasing once per cycle, so lease round trips scale with time rather than with write volume
  (two replicas over one live Redis store at 3000 writes in five seconds previously performed
  2300 acquires). `StateChangeDispatcher` is now `IAsyncDisposable` and exposes an idempotent
  `ReleaseLeaseAsync()`; the hosted worker releases on every loop exit, before every backoff
  delay, and on disposal. **An application that drives `StateChangeDispatcher.DispatchOnceAsync`
  itself must now dispose the dispatcher**, or a standby replica waits out `LeaseTtl` instead of
  one `PollInterval`. No new option. One replica now performs all dispatch until it stops or
  dies, where the previous per-cycle acquire let replicas share load by accident.
- **`ManualTimeProvider` now overrides `CreateTimer`**, so timers created from it fire on
  `Advance`/`SetUtcNow` rather than on the system clock. It previously inherited `TimeProvider`'s
  base implementation, which schedules a real `System.Threading.Timer` — meaning a caller who passed
  it to `new PeriodicTimer(interval, provider)` got a timer that silently ignored `Advance`. **A test
  that was relying on that wall-clock behaviour will now hang or time out** and should advance the
  clock instead. Callbacks fire in due-time order, once per elapsed period, and are invoked outside
  the provider's lock so a callback may call `Advance` itself. A `period` of `TimeSpan.Zero` is
  treated as one-shot.
- **The Entity Framework Core provider's SQL Server key-length limit is now stated rather than
  discovered.** `StatesmanHeads`' primary key is `Root` (128) + `Path` (512) + `Partition` (256)
  `nvarchar`, up to 1792 bytes, and `StatesmanRecords` adds `Revision` for 1800 — against SQL
  Server's 900-byte clustered index key limit. `CREATE TABLE` succeeds with a warning, so
  `EnsureCreated` and any consumer migration work, and every realistic address stores; an address
  whose key genuinely exceeds 900 bytes fails at insert with `DbUpdateException`. The shipped column
  lengths are unchanged: both packages ship no migrations, so shortening a column would change every
  consumer's schema. A deployment that needs longer addresses should shorten `Path` or `Partition` in
  its own model configuration. PostgreSQL has no such limit.
- **`Statesman.Persistence.EntityFrameworkCore` and `Statesman.Outbox.EntityFrameworkCore` each gain
  two public types**: `Statesman.StatesmanMigrationsAssembly` /
  `Statesman.Outbox.EntityFrameworkCore.StatesmanOutboxMigrationsAssembly`, a subclass-tolerant
  `IMigrationsAssembly` replacement, and `Statesman.StatesmanLedgerMigrations` /
  `Statesman.Outbox.EntityFrameworkCore.StatesmanOutboxMigrations`, each holding a
  `HistoryTableName` constant, a `UseStatesman…Migrations(this DbContextOptionsBuilder, Assembly)`
  extension, and a `BaselineAsync(DbContext, CancellationToken = default)` helper for a database
  created by `EnsureCreated`. Purely additive: no existing signature changes, no storage format
  changes, and a consumer who never calls any of it sees exactly today's behaviour. Both
  migrations-assembly types derive from Entity Framework Core's internal `MigrationsAssembly`
  (`EF1001`), so an Entity Framework Core version bump that changes or removes that internal type is
  the one thing that can break this feature — the pragma disabling `EF1001` is scoped to the two type
  declarations, never a project-level `NoWarn`, so a bump that breaks it fails the build at exactly
  those lines rather than silently.

### Fixed

- **`EnableRetryOnFailure` is now a supported configuration for
  `EntityFrameworkStateLedgerStore<TContext>`, and fixes a related bug the retry makes reachable.**
  Its four transactional methods — `AppendAsync`, `AcquireAsync`, `CaptureAsync` and `ImportAsync` —
  run their attempt bodies inside `DbContext.Database.CreateExecutionStrategy()`, so a retrying
  strategy re-runs a whole attempt instead of refusing the user-initiated transaction with
  `InvalidOperationException`. Previously every one of those methods threw on the first call, which
  made the store unusable in the shape every Entity Framework Core deployment guide recommends for
  SQL Server and PostgreSQL. The store's own bounded retry is unchanged and still sits outside the
  strategy: the two layers own different failure classes, the provider's transient connection
  failures against the one-time creation of the global-position row, which no provider classifies as
  transient. Behaviour under the default non-retrying strategy is unchanged.
  `Statesman.Outbox.EntityFrameworkCore` needed no change — it opens no transaction — and its doc
  comment now says so instead of claiming there is no live server test job. The retry also makes one
  lost-acknowledgement window reachable for the first time: when a lease insert commits on the server
  but its acknowledgement is lost, a replayed attempt re-reads a live lease row carrying this caller's
  own token, so `AcquireAsync` now returns that lease rather than incorrectly returning `null`. The
  lease token is generated once per call rather than once per attempt, which is what makes the row
  recognisable.
- the filesystem provider's change-log append is now fsynced whenever
  `FileSystemStateLedgerStoreOptions.FlushToDisk` is set (the default), closing a durability hole
  where the provider's own flush option covered the history and head writes but not the structure
  the change feed treats as its commit point — a host or power failure could previously lose every
  change-log line the OS had not written back while the corresponding history files survived. No
  new option: `FlushToDisk` governs all three writes. **The cost is measurable and lands on
  append throughput**: the new fsync sits inside the provider's global change-log gate, so the
  serialized portion of an append goes from one fsync to two. See `docs/providers/index.md` for
  the re-measured per-append figure. The filesystem feed's one remaining residual is the crash
  window between the record write and the change-log append, which fsync does not address.
- **The filesystem provider's change feed is read incrementally.** `FileSystemStateLedgerStore`
  keeps a private in-process index of its `_changes.log`, so `ReadAsync` parses only the bytes
  appended since the previous read on the same store instance rather than the whole file every
  time. This closes the regression paging introduced in the same release: an outbox draining a
  backlog of N records paid about N/`BatchSize` whole-log scans, quadratic in backlog size,
  and now pays one parse plus the suffix. It also shortens the window the provider's global
  change-log gate is held, so the cost was a write-throughput problem as much as a
  read-latency one. `StateChangeCursor` is unchanged and no cursor store is migrated: the
  index lives in the store instance, not in the cursor. It is discarded and rebuilt whenever
  the log shrinks or its remembered tail bytes stop matching, so truncation — the documented
  recovery from a corrupt log — and any future compaction stay safe, and it is per process, so
  a second process reading the same directory pays its own first parse. **The cost is resident
  memory**: about 40 bytes per change-log line plus one shared set of strings per distinct
  address, held for the store's lifetime. Measured figures are in `docs/providers/index.md`.
- **Entity Framework Core: concurrent appends to different addresses no longer fail on a server
  engine.** The global position is now allocated by a single atomic increment taken as the first
  statement of the append and import transactions, so every writer serializes on that one row in a
  single lock order and both transactions run at read-committed isolation. Previously, on SQL Server
  two appends for different addresses could deadlock on a shared key-range lock over
  `PK_StatesmanHeads` and one was chosen as the deadlock victim, and on PostgreSQL the second
  appender was aborted with `40001`; both surfaced to the caller as an `InvalidOperationException`.
  Positions remain dense — a rejected append rolls its increment back — and the feed's ordering
  guarantee is strengthened, because position order is now exactly commit order. SQLite behaviour is
  unchanged.
- **Entity Framework Core: a restore no longer fails or stalls when a prune removes the record it is
  replacing.** `ImportAsync` makes up to three attempts at a lost write race and re-inserts the record;
  previously the import failed with `40001` on PostgreSQL and blocked for the command timeout on SQL
  Server.
- **Entity Framework Core: `StateCaptureConsistency.SnapshotDistributed` is now a snapshot on SQL
  Server.** It maps to `IsolationLevel.Snapshot` there, `RepeatableRead` on PostgreSQL, and
  `Serializable` on SQLite and any other provider. `Serializable` on SQL Server is lock-based rather
  than versioned, so a capture could previously return one address's pre-write revision beside
  another's post-write revision. SQL Server deployments must have
  `ALTER DATABASE … SET ALLOW_SNAPSHOT_ISOLATION ON`.
- **The filesystem provider no longer yields a re-imported record at two positions.** Re-importing an
  existing revision at a different `GlobalPosition` rewrites its history file in place while the
  change log only grows, so the earlier log line kept dereferencing the rewritten record — and the
  two envelopes disagreed with themselves, the cursor carrying the old position while
  `Record.GlobalPosition` carried the new one. `CompactChangeLogAsync` drops any line whose history
  file carries a different position, which collects it. **Redis and the in-memory provider carried
  this same residue as of the previous release; this release fixes both** — see "Redis and the
  in-memory provider no longer leave a stale change-feed entry…" and "Redis no longer evicts another
  address's change-feed entry…" below.
- **The filesystem provider no longer fails a write on Windows when a reader has the record file
  open.** `AtomicWriteAsync` replaced head and history files with `File.Move(…, overwrite: true)`,
  which on Windows fails with `ERROR_ACCESS_DENIED` against a destination any handle holds open, and
  history files were read with `File.OpenRead`, which grants no `FILE_SHARE_DELETE`. A change-feed
  read holds each history file open for the length of one deserialization, so a re-import of that
  revision could fail with `UnauthorizedAccessException` and a concurrent `PruneAsync` with
  `IOException`. Record files are now replaced by the same POSIX-semantics rename compaction uses and
  read with `FileShare.Delete`. No storage-format change, no behaviour change on Linux or macOS, and
  no change to what this provider supports across process boundaries.
- **Redis and the in-memory provider no longer leave a stale change-feed entry when an import moves a
  revision to a new `GlobalPosition`.** Re-importing an existing revision at a different position
  replaced the history record and left the earlier feed entry behind, so one record yielded at two
  positions with a single history twin. Both providers now remove the earlier entry by member as part
  of the same atomic write. The filesystem provider is unchanged and its repair remains
  `CompactChangeLogAsync`, because its change log is append-only.
- **Redis no longer evicts another address's change-feed entry when an import lands on its
  `GlobalPosition`.** A colliding-lineage restore can legitimately put two addresses at one position;
  Redis removed the incumbent by position score, taking a record out of the change feed while its
  history record survived. The removal is now member-exact, which is the same reasoning `PruneAsync`
  has used since 0.3's feed-retention work. Two new shared conformance tests pin both behaviours
  across every provider that accepts imports.
- **The filesystem partition catalog no longer lists a partition that was never committed.**
  `ListPartitionsAsync` now lists an address only when its head file exists — the artifact both
  `AppendAsync` and `ImportAsync` write and nothing deletes — instead of trusting the change log's
  highest recorded position with no dereference to a stored record. A torn final line in the change
  log (a crash mid-append, or a cross-process reader catching one in flight) that happens to keep
  five tab-separated fields with parseable numbers no longer produces a phantom descriptor;
  `IStateChangeFeed.ReadAsync` already filtered the same phantom out by a different route.
- **The maintenance-failure retention queue's trim is now atomic under concurrent reporters.** The
  bound introduced above (see `### Added`) closes a check-then-act race where two reporters could
  each observe the same over-the-bound count and both trim for it, retaining fewer than 64 entries;
  a dedicated lock around the enqueue-and-trim body removes it.
- **Both migrations-assembly types tolerate a `[DbContext]` attribute declared at two levels of a
  migration's class hierarchy**, instead of throwing `AmbiguousMatchException` during discovery.
  Applies identically to `Statesman.StatesmanMigrationsAssembly` and
  `Statesman.Outbox.EntityFrameworkCore.StatesmanOutboxMigrationsAssembly`.
- **Both migrations-assembly types now log `MigrationAttributeMissingWarning`** for a
  context-targeted migration missing its `[Migration]` id, instead of silently skipping it —
  matching the behaviour of the Entity Framework Core base class each type replaces.
- **Redis's `ListPartitionsAsync` now honours a cancelled token before issuing `HGETALL`**, instead
  of fetching the whole partition hash first and checking cancellation only inside the yield loop,
  and reuses the store's existing `DeserializePartition` helper instead of a second, independently
  maintained inlined `JsonSerializer.Deserialize` call.

### Known limitations

- PostgreSQL stores `timestamptz` to the microsecond, so `OccurredAt`, `FreshUntil`, `ServeUntil` and
  lease `ExpiresAt` lose the last digit of a .NET tick on that engine. SQL Server's 900-byte
  clustered index key limit applies to the shipped composite key.

## [0.3.0] - 2026-09-08

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
- `IStateChangeFeed` durable, cursor-resumable cross-stream change feed capability, implemented natively by all five providers (EF Core over its existing indexed column; Redis via a new global sorted set; filesystem via a new append-only index; in-memory via a queue; tiered delegating to cold), lossless within retention — see `docs/providers/index.md`'s "Change feed semantics and limitations" for the retention/prune caveat and other per-provider trade-offs before building on it
- `IPartitionCatalog` partition-discovery capability, implemented natively by all five providers (in-memory reusing its existing `_streams` map; EF Core enumerating `StatesmanHeads`; Redis via a new hash written by the same server-side append script as the change-feed entry, with `MULTI`/`EXEC` still covering import and distributed capture; filesystem reusing the change-feed log; tiered delegating to cold) — see `docs/providers/index.md`'s "Partition catalog semantics and limitations" for the filesystem provider's post-crash staleness window and its O(total writes) listing cost
- `IDistributedCapture` coherent multi-address capture capability, implemented by the Entity Framework Core provider (a transaction whose isolation level is mapped from the requested consistency) and the Redis provider (a `MULTI`/`EXEC` batch of head reads), with tiered delegating to cold; the filesystem and in-memory providers do not implement it. `IStatesman.CaptureAsync` and `IStateContainer.CaptureAsync` gain a `StateCaptureConsistency required` parameter defaulting to `ProcessLocal`, so existing call sites keep today's behavior unchanged — see `docs/providers/index.md`'s "Distributed capture semantics and limitations" for the per-store scope of the snapshot guarantee, Redis Cluster's `CROSSSLOT` limit, and SQLite's write-stall trade-off under `SnapshotDistributed`
- `IReplicationLagSource` replication-lag capability, implemented only by the tiered provider, which compares its hot replica against its cold authority through both tiers' `IPartitionCatalog` and reports the newest head position of each tier plus the number of partitions the replica lacks or holds stale as a `StateReplicationLag`; the estimate reads hot before cold so it is designed to over-report rather than under-report lag under concurrent writes — see `docs/providers/index.md`'s "Replication lag semantics and limitations" for why `PositionGap` is a distance in a sparse sequence rather than a record count and why lazily-populated partitions count as behind
- `IStateLedgerReplica` on the Redis provider: an exact, idempotent import that writes head, revision guard, history, change feed, and partition hash in one transaction and raises the global position counter to at least the imported position first, so Redis can serve as a tiered hot replica or a restore target; positions above 2^52 are refused because the change feed's sorted-set scores are IEEE doubles, so a filesystem export (UTC-tick positions) cannot be restored into Redis
- `Statesman.Tooling` package: `StateLedgerExport` writes one root's retained ledger history (via `IPartitionCatalog` plus per-partition history, not the change feed) to a newline-delimited JSON file stamped with the declaration fingerprint and format version; `StateLedgerRestore` validates the whole file against the target's manifest before importing anything, refuses on any mismatch or truncation, requires `IStateLedgerReplica`, and refuses a non-empty target by default — see `docs/guides/backup-restore.md`
- `Statesman.Outbox` and `Statesman.Outbox.Redis` packages: a lease-gated outbox that reads a store's `IStateChangeFeed` from a persisted, monotonically-advancing cursor and publishes each record to an `IStateChangeSink` as a `statesman.state-change/v1` message, at least once, keyed for deduplication by `{store}/{address}#{revision}`. `OutboxOptions.RequireLease` defaults to `true`, so a store without `IStateLeaseProvider` is refused by name rather than silently dispatching from two processes; the worker is the repository's first production caller of `IStateLease.RenewAsync`. The Redis bridge publishes to a stream with the explicit entry id `{globalPosition}-0`, which makes a re-publish after a crash a server-side no-op. Entity Framework Core cursor storage is deliberately not shipped, so no existing consumer needs a second migration — see `docs/guides/outbox.md`'s "What \"delivered\" means" for the feed's prune caveat, which the outbox inherits and cannot fix, plus the filesystem provider's change-log fsync gap and the lease residual on the filesystem and in-memory providers, and for which configurations (any provider with `StateRetentionPolicy.KeepAll`) are complete for feed losslessness despite those residuals
- `IStateChangeFeed` is now lossless within retention on every provider: a record's `GlobalPosition` is allocated in the same step that makes the record visible to the feed, so a consumer resuming from the cursor of the last record it accepted is never skipped past a record. The in-memory provider allocates and publishes under one lock; the filesystem provider allocates, writes the history file, and appends its change-log line under one global gate (writing the head file outside it), which globally serializes one `WriteThrough` fsync per append and is a real append-throughput change operators will measure; Redis moves `INCR` and all five writes into one Lua script, which also means a rejected conditional append no longer burns a position, so Redis positions are dense. Redis stores `globalPosition` as the last JSON property of a record — a write-order change only, with deserialization unaffected, so no stored data needs migrating. Entity Framework Core already allocated at commit time and is unchanged, with a new regression guard pinning the `StatesmanSequences` row mechanism that makes it safe. Retention is now the only remaining feed caveat, plus a filesystem crash window between the record write and the change-log append — see `docs/providers/index.md`'s "Change feed semantics and limitations"
- `IStateChangeNotifier`, an optional capability that pushes a low-latency hint that a store's `IStateChangeFeed` may have advanced. Implemented by the in-memory provider (one bounded capacity-1 drop-write channel per subscriber, published after the feed lock is released so a subscriber can never apply backpressure to a writer), by Redis (`PUBLISH` to `{KeyPrefix}:{name}:notifications` after the append script returns and after an import commits, subscribed through a `ChannelMessageQueue`), and by the tiered provider, which implements it directly and delegates to its cold tier so a hot-tier hint can never describe a feed no consumer reads. The filesystem and Entity Framework Core providers deliberately do **not** implement it: the filesystem provider has no reliable cross-process notification primitive to match the change log that is its only channel to another process, and the Entity Framework Core package is provider-neutral while every push mechanism is engine-specific. A notification is a latency hint only — payload-free by design, carrying no delivery guarantee, possibly coalesced or lost, and never a source a consumer may advance its cursor from; the feed remains the authority and is lossless within retention. `Statesman.Outbox`'s hosted worker now subscribes when the store has the capability and dispatches on a hint instead of waiting out `OutboxOptions.PollInterval`, which becomes the floor rather than the only trigger; there is no new option, a worker in lease standby keeps to the interval, and two cycles never run at once. See `docs/providers/index.md`'s "Change notification semantics and limitations"

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
- the filesystem provider's change-log parser no longer throws `IndexOutOfRangeException` or `FormatException` out of `IStateChangeFeed.ReadAsync` or `IPartitionCatalog.ListPartitionsAsync` when it reads a partially-written final line, which a reader in another process can observe because the log append is not atomic. A malformed **final** line is skipped, because an in-flight tear like that completes on its own and the next read sees the whole line. A tear that is already **durable** — what a crash mid-append leaves on disk — never completes, so the change-log append now starts a fresh line whenever the log's last byte is not a newline, rather than merging the new record into the partial line, where the merged line would still be last, would be skipped in turn, and would cost one committed record from a feed documented as lossless with no error anywhere. The torn line then stops being the final one and the next read throws `InvalidDataException` naming the file and the line; recover by truncating that trailing partial line. A malformed line anywhere else is corruption and throws the same way, rather than skipping it, which would drop records from a lossless feed

### Notes

This archive was assembled in an environment without a .NET SDK. The offline structural validator is included and its report is packaged, but compilation and execution must be performed by the included build scripts or CI.
