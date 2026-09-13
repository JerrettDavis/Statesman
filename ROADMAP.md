# Statesman roadmap

Statesman is being developed as a small authoritative core surrounded by optional capabilities. A capability only belongs in the shared abstraction when every provider can make the same promise. Stronger guarantees are exposed through separate interfaces and provider negotiation rather than implied by a convenient method name.

## 0.1: executable declaration and local authority

The repository currently implements the first coherent vertical slice:

- immutable, fingerprinted declarations and typed state keys
- root, container, and partition boundaries
- proactive whole-state and facet loaders
- reactive set, update, invalidate, clear, and typed interactions
- current, stale-aware, and forced-fresh reads
- optimistic revisions, single-flight refresh, and local coherent capture
- append-only history with revision, age, byte, and tombstone retention
- in-memory, filesystem, Redis, EF Core, and tiered stores
- typed and root observation streams plus operation subscriptions
- ASP.NET Core inspection and signal endpoints
- mutation and key-determinism analyzers
- deterministic test harnesses, provider tests, end-to-end tests, CI, and packaging

This release is intentionally a preview. Its public concepts are deliberate, but package compatibility is not promised until the 1.0 criteria are met.

## 0.2: contract hardening

The next milestone focuses on proving behavior across providers rather than adding breadth.

- shared provider conformance suite covering compare-and-swap, exact imports, retention, ordering, cancellation, and corruption behavior — the compare-and-swap, cancellation, import-rejection, partition-discovery, exact-position-import, provider push notification, and distributed capture contracts shipped as Phase 16 items in [the 0.3 design spec](docs/superpowers/specs/2026-09-03-roadmap-0.3-distributed-coordination-design.md): `tests/Statesman.Conformance.Tests` now carries one shared abstract suite per contract (`LedgerWriteConformanceTests`, `CancellationConformanceTests`, `ImportRejectionConformanceTests` — the corruption contract's import-refusal half — `PartitionCatalogConformanceTests`, `LedgerReplicaConformanceTests`, `ChangeNotifierConformanceTests`, `DistributedCaptureConformanceTests`), each run against all five built-in providers (in-memory, filesystem, Redis, Entity Framework Core, tiered) through one construction point, `ConformanceProviders`, alongside the change-feed and lease suites already sharing that fixture. Every suite carries a break-the-mechanism proof pairing a wrong double that fails the shared assertion with a correct one that passes, and every capability gap is an honest `Assert.SkipUnless` skip rather than a silent pass — filesystem and Entity Framework Core skip the notifier suite (no cross-process push primitive), in-memory and filesystem skip distributed capture (no cross-process guarantee to offer), and the tiered provider skips every import-only fact (it vetoes `IStateLedgerReplica` outright). Retention semantics (`MaxAge`/`MaxRevisions`/`MaxBytes` pruning) are not yet in the shared suite, and provider-specific corruption behaviour beyond import refusal (a torn file, a truncated stream) stays deliberately per-provider, because each provider's failure shape is a property of its own storage format. `Statesman.Conformance.Tests` reached `total: 219` (`153` succeeded, `66` honest skips without live Redis; `196` succeeded, `23` skips with it), `failed: 0` on SQLite and live Redis alike.
- load diagnostics with structured per-source timing and health summaries — considered for Phase 16 and parked to Phase 17: per-source timing means deciding whether a timing is stored metadata or a side channel, and a "health summary" type overlaps the health check bullet 3 ships below; that health check is built so a load-diagnostics summary lands as additional `HealthCheckResult.Data` keys rather than a shape change.
- OpenTelemetry semantic conventions, health checks, and rate-limited maintenance failure reporting — shipped as Phase 16 items in [the 0.3 design spec](docs/superpowers/specs/2026-09-03-roadmap-0.3-distributed-coordination-design.md): a per-store token bucket (`StatesmanDiagnostics.MaintenanceFailureRate` = 16 per `StatesmanDiagnostics.MaintenanceFailureRateWindow` = one minute) now gates maintenance-failure retention ahead of the existing 64-entry bound, reported through a new `statesman.maintenance.failures.suppressed` counter alongside the two Phase 15 counters, and reachable through a new public surface, `IStatesmanDiagnostics` (`ReadMaintenanceFailures()`, `ClearMaintenanceFailures()`), discovered via `IStatesmanDiagnosticsExtensions.TryGetDiagnostics` rather than a new `IStatesman` member, so no baselined interface breaks. `StatesmanHealthCheck` (`Statesman.Extensions.Hosting`) reports degraded when a store's maintenance failures are being suppressed or dropped, or a store runs interval maintenance without a lease, and unhealthy while a root has not finished initializing; because `IHealthChecksBuilder`/`AddCheck` live only in the larger, non-abstractions health-checks package and not the `.Abstractions` package `Statesman.Extensions.Hosting` references, it ships without an `AddStatesmanHealthCheck` extension — register with `services.AddHealthChecks().AddCheck<StatesmanHealthCheck>("statesman")`. A new pinning test, `StatesmanTelemetryConventionTests`, asserts the exact name, kind, unit and tag-key set of every documented instrument on the `Statesman` meter, discovered through the meter's own static initializer rather than a test-created decoy, so a renamed or re-unit'd instrument fails a named test instead of drifting silently — this is the OpenTelemetry semantic-conventions check, documented alongside two accepted 0.x deviations on the new `docs/reference/telemetry.md`.
- serializer envelopes with content type, serializer id, and migration provenance
- filesystem recovery tooling (compaction shipped in 0.3)
- Redis cluster and script compatibility tests
- EF Core migrations for SQL Server, PostgreSQL, and SQLite shipped in 0.3, as six packages — `Statesman.Persistence.EntityFrameworkCore.{Sqlite,SqlServer,PostgreSQL}` and `Statesman.Outbox.EntityFrameworkCore.{Sqlite,SqlServer,PostgreSQL}`, three per context because the three engines share no type mapping — and retry-on-failure became a supported configuration there rather than guidance. Built as a Phase 14 item in [the 0.3 design spec](docs/superpowers/specs/2026-09-03-roadmap-0.3-distributed-coordination-design.md): `MigrationsAssembly` and `MigrationsHistoryTable` as the seam, a baseline history row — not a baseline migration — for consumers already on `EnsureCreated`, and an xUnit drift test — not `dotnet ef migrations has-pending-model-changes` in CI — as the exit criterion. Shipped migrations stay opt-in; the consumer-owns-the-migration arrangement both contexts document today remains the supported default.
- analyzer code fixes and stronger collection-mutation data-flow analysis
- public API compatibility baselines and package-validation gates — shipped as a Phase 15 item in [the 0.3 design spec](docs/superpowers/specs/2026-09-03-roadmap-0.3-distributed-coordination-design.md): `PackageValidationBaselineVersion` baselines the fifteen packages `v0.3.0` published, at `0.3.0`, set once in `src/Directory.Build.props`; the seven packages with no `0.3.0` release — `Statesman.Outbox.EntityFrameworkCore`, added after the tag, and the six Phase 14 migration packages — blank the property in their own project file. The two deliberate breaks this baseline records, `IStateChangeFeed.ReadAsync` (0.3's previous phase) and `Statesman.Outbox.OutboxCursorFile` (this phase), are recorded as seven committed `CompatibilitySuppressions.xml` files, and every entry is explained on the new `docs/reference/api-compatibility.md` rather than in the generated XML, because a hand-added XML comment does not survive regeneration. The gate runs on `dotnet pack` in both `ci.yml`'s `pack` job and `release.yml`, with no workflow edit, because both already restore before packing.

With bullets 1 and 3 substantially shipped in Phase 16, every ROADMAP 0.2 bullet has been addressed, though not every one is complete. Bullet 1 (shared provider conformance) now runs one suite across all five built-in providers for compare-and-swap, cancellation, import rejection (the corruption contract's import-refusal half), partition discovery, exact-position import, provider push notification, and distributed capture — `total: 219`, `153` succeeded / `66` honest skips without live Redis, `196` / `23` with it, `failed: 0` on SQLite and live Redis, and on SQL Server and PostgreSQL for the four Entity Framework Core-affected projects — but retention semantics (`MaxAge`/`MaxRevisions`/`MaxBytes` pruning uniformity) are not yet in the shared suite, and provider-specific corruption beyond import refusal stays deliberately per-provider. Bullet 2 (load diagnostics) is unstarted and parked to Phase 17, since a "health summary" type would have overlapped bullet 3's health check by a month. Bullet 3 (OpenTelemetry semantic conventions, health checks, and rate-limited maintenance failure reporting) is now fully shipped: a per-store rate limiter ahead of the existing retention bound, a public `IStatesmanDiagnostics` surface, `StatesmanHealthCheck`, and a pinning test over the meter's documented instruments and tag keys. Bullet 4 (serializer envelopes) is unstarted. Bullet 5 (filesystem recovery tooling) has only the compaction shipped in 0.3. Bullet 6 (Redis cluster coverage) has script compatibility covered, but cluster coverage is not. Bullet 8 (analyzer code fixes and stronger collection-mutation data-flow analysis) is unstarted. The 0.3 design spec's Phase 16 section lists what is parked and why, under "Explicitly parked, with reasons". Of the exit criteria below: "CI builds every supported target framework" is fully met; "all built-in providers pass one conformance suite" is met for the contracts the shared suite now covers and not met for retention conformance or general corruption beyond import refusal; "upgrade and corruption scenarios are documented" remains partial — corruption is documented per provider and upgrade is documented for Entity Framework Core schema migration and declared-state migration, but no operator runbook for a store-format upgrade or restore-from-corruption exists. Of the nine bullets above, the EF Core migrations bullet, the API baseline bullet, and this phase's bullet 3 are fully shipped. **0.2 is not complete.**

Exit criteria: all built-in providers pass one conformance suite; upgrade and corruption scenarios are documented; CI builds every supported target framework.

## 0.3: durable distributed coordination

Distributed behavior will be explicit and capability-based.

- `IStateChangeFeed` with durable cursors and resumable history projection
- provider-native notifications as an optimization over ledger cursors
- leases for interval loaders and singleton maintenance workers
- partition discovery for stores that can support it efficiently
- distributed coherent capture capability with a documented consistency level
- import/export and restore tooling with fingerprint and schema validation
- replication lag metadata and read policies for tiered or replicated deployments
- outbox-oriented bridges for message brokers without making observation a broker

Exit criteria: no distributed method silently falls back to process-local semantics; capability discovery tells callers exactly which guarantee is available.

## 0.4: state graph and orchestration

Statesman can grow from isolated authorities into an inspectable state graph without becoming a workflow engine.

- declared selectors and derived state with dependency manifests
- cycle detection and deterministic invalidation propagation
- materialized versus ephemeral derived-state policies
- typed effects that execute after accepted transitions with idempotency metadata
- batch declarations and generated strongly typed accessors
- source-generated manifests for Native AOT and startup-sensitive applications
- Blazor, WPF, WinUI, MAUI, and reactive binding adapters built over the same observation contracts

Exit criteria: derived behavior appears in the manifest, can be visualized, and cannot create hidden writes or dependency cycles.

## 1.0: stable authority contract

A 1.0 release requires:

- a reviewed and compatibility-baselined public API
- conformance coverage for every first-party provider
- documented upgrade, backup, restore, and schema migration procedures
- stable manifest canonicalization and versioning rules
- performance baselines for high-cardinality partitions and long histories
- security review of inspection endpoints, provider inputs, and serialization boundaries
- analyzer false-positive and suppression guidance validated against real migrations
- at least one production adoption that exercises multi-source loading, durable history, and rolling upgrades

## Deliberate non-goals

The roadmap does not turn Statesman into a general database, object-relational mapper, arbitrary query engine, distributed transaction coordinator, message broker, or event-sourcing framework. Integrations with those systems remain adapters around the state authority. Any proposal that weakens this boundary must explain why it cannot be an optional capability or external projection.
