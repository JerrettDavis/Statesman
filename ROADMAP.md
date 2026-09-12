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

- shared provider conformance suite covering compare-and-swap, exact imports, retention, ordering, cancellation, and corruption behavior
- load diagnostics with structured per-source timing and health summaries
- OpenTelemetry semantic conventions, health checks, and rate-limited maintenance failure reporting
- serializer envelopes with content type, serializer id, and migration provenance
- filesystem recovery tooling (compaction shipped in 0.3)
- Redis cluster and script compatibility tests
- EF Core migrations for SQL Server, PostgreSQL, and SQLite shipped in 0.3, as six packages — `Statesman.Persistence.EntityFrameworkCore.{Sqlite,SqlServer,PostgreSQL}` and `Statesman.Outbox.EntityFrameworkCore.{Sqlite,SqlServer,PostgreSQL}`, three per context because the three engines share no type mapping — and retry-on-failure became a supported configuration there rather than guidance. Built as a Phase 14 item in [the 0.3 design spec](docs/superpowers/specs/2026-09-03-roadmap-0.3-distributed-coordination-design.md): `MigrationsAssembly` and `MigrationsHistoryTable` as the seam, a baseline history row — not a baseline migration — for consumers already on `EnsureCreated`, and an xUnit drift test — not `dotnet ef migrations has-pending-model-changes` in CI — as the exit criterion. Shipped migrations stay opt-in; the consumer-owns-the-migration arrangement both contexts document today remains the supported default.
- analyzer code fixes and stronger collection-mutation data-flow analysis
- public API compatibility baselines and package-validation gates — shipped as a Phase 15 item in [the 0.3 design spec](docs/superpowers/specs/2026-09-03-roadmap-0.3-distributed-coordination-design.md): `PackageValidationBaselineVersion` baselines the fifteen packages `v0.3.0` published, at `0.3.0`, set once in `src/Directory.Build.props`; the seven packages with no `0.3.0` release — `Statesman.Outbox.EntityFrameworkCore`, added after the tag, and the six Phase 14 migration packages — blank the property in their own project file. The two deliberate breaks this baseline records, `IStateChangeFeed.ReadAsync` (0.3's previous phase) and `Statesman.Outbox.OutboxCursorFile` (this phase), are recorded as seven committed `CompatibilitySuppressions.xml` files, and every entry is explained on the new `docs/reference/api-compatibility.md` rather than in the generated XML, because a hand-added XML comment does not survive regeneration. The gate runs on `dotnet pack` in both `ci.yml`'s `pack` job and `release.yml`, with no workflow edit, because both already restore before packing.

With this bullet shipped, every ROADMAP 0.2 bullet has been addressed, though not every one is complete. Bullet 1 (shared provider conformance) has per-provider tests, but they are not yet lifted into `tests/Statesman.Conformance.Tests`. Bullet 2 (load diagnostics) is unstarted. Bullet 3 (OpenTelemetry semantic conventions and health checks) is unstarted; maintenance-failure reporting is now bounded and countered, but not yet rate-limited or exposed through a public surface. Bullet 4 (serializer envelopes) is unstarted. Bullet 5 (filesystem recovery tooling) has only the compaction shipped in 0.3. Bullet 6 (Redis cluster coverage) has script compatibility covered, but cluster coverage is not. Bullet 8 (analyzer code fixes and stronger collection-mutation data-flow analysis) is unstarted. The 0.3 design spec's Phase 15 section lists what is missing from each, under "Explicitly parked, with reasons". Of the exit criteria below, only "CI builds every supported target framework" is fully met, and of the nine bullets above, only the EF Core migrations bullet and this one are fully shipped. **0.2 is not complete.**

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
