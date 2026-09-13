# Operational observability

A production Statesman deployment should make state authority inspectable without exposing state payloads by default.

## Recommended signals

Track refresh latency and fault rate by state path, source, and root. Watch optimistic conflict rate because a sustained increase often indicates a hot partition or an external writer. Track stale observations, partial loads, retained last-known faults, pruning failures, and tiered-cache repair failures separately.

Provider health and state health are different. A Redis connection can be healthy while one upstream loader is degraded. A loader can be healthy while retention repeatedly fails. Dashboards should preserve those distinctions.

## Cardinality

Partition identifiers can be unbounded and personally identifying. Do not place raw user, tenant, or device partitions in metrics unless the cardinality and data policy are explicitly acceptable. Traces or structured logs with sampling are usually a better place for a specific partition.

## Inspection endpoints

The ASP.NET Core package can expose manifest, snapshot, history, and signal routes. Attach an authorization policy. Keep values hidden unless the consumer genuinely needs them. Signals are writes and remain disabled by default.

## Maintenance failures

The runtime counts provider pruning failures after successful writes, through `statesman.maintenance.failures` and `statesman.maintenance.failures.dropped` on the `Statesman` meter, and tiered stores expose their most recent cache error. Surface those counters through your normal metrics and health infrastructure. The runtime also retains the most recent failures for an application to read. `IStatesman` exposes them through `StatesmanDiagnosticsExtensions.TryGetDiagnostics`, which yields a `MaintenanceFailureDiagnostics` snapshot carrying the lifetime counts, the retained failures with the store name and time of each, and the stores running maintenance without a lease; `ClearMaintenanceFailures` drains them. Retention is bounded at `StatesmanDiagnostics.MaxRetainedMaintenanceFailures` and rate-limited to `StatesmanDiagnostics.MaintenanceFailureRate` failures per ledger store per `StatesmanDiagnostics.MaintenanceFailureRateWindow`. The rate limit gates retention only: an accepted append stays accepted, `statesman.maintenance.failures` still counts every failure, and the two subordinate counters say which mechanism discarded what — `statesman.maintenance.failures.suppressed` for the rate limit and `statesman.maintenance.failures.dropped` for the bound.

## Load diagnostics

Every completed loader-driven refresh is timed on the runtime's own clock. Three bounded keys land on
the stored record — `statesman.load.duration.ms`, `statesman.load.started` and
`statesman.load.completed` — so an operator reading a history record months later can see how long
that load took. The per-source breakdown is deliberately not on the record: read it from
`IStatesmanDiagnostics.ReadLoadDiagnostics()`, which keeps the latest report per address bounded at
`StatesmanDiagnostics.MaxRetainedLoadReports`, or from the `statesman.load.source.duration` histogram,
which carries a `statesman.source` tag alongside the usual four. Prefer the histogram for dashboards
and the surface for a health check or an incident: the surface is bounded and a restart clears it.

A load that completes with one or more faulted sources reports completeness `partial`, or
`initial-fallback` when none contributed and a declared initial value carried it. Both are degraded
rather than failed — the state is usable and authoritative — and `StatesmanHealthCheck` reports them
as Degraded for exactly that reason.
