# Diagnostics and metadata

Statesman exposes diagnostics through snapshots, activity traces, metrics, manifests, and analyzer diagnostics.

## Snapshot errors

`loader-failed` represents an acquisition attempt that produced no usable new state. Depending on policy, the fault record can retain the last-known payload.

`partial-load` represents a best-effort acquisition where at least one declared source succeeded or an initial fallback remained usable, but one or more sources failed. The snapshot stays `Ready` when it contains a newly accepted usable value, while `Error` makes the degradation visible.

## Load metadata

Loader records reserve the `statesman.*` prefix. Current keys include:

- `statesman.load.completeness`: `complete`, `partial`, `initial-fallback`, `seeded`, or `retained`
- `statesman.load.sources.ready`
- `statesman.load.sources.faulted`
- `statesman.initial.status`
- `statesman.source.<normalized-name>.status`: `ready` or `faulted`
- `statesman.load.duration.ms`: how long the whole load took, in milliseconds, invariant culture
- `statesman.load.started`: when the load began, round-trip (`O`) format, invariant culture
- `statesman.load.completed`: when it finished, same format; always `started` plus the duration

Timing is measured on the runtime's own `TimeProvider`, so a test driving a `ManualTimeProvider` reads
exact elapsed values rather than real-time bounds. These three keys are bounded: they do not grow with
the number of sources a state declares. **Per-source timing is deliberately not on the record** — a key
per source would scale permanent storage with the declaration, and every provider persists this
dictionary on the head record and on every history record. Read per-source timing from the
[load diagnostics surface](#load-diagnostics) instead, or from the `statesman.load.source.duration`
meter instrument.

Application metadata is merged first and reserved loader metadata wins, preventing a caller from falsely reporting loader health.

## Load diagnostics

`IStatesman` exposes structured load reports through
`StatesmanDiagnosticsExtensions.TryGetDiagnostics`, the same discovery call the maintenance-failure
surface uses. `IStatesmanDiagnostics.ReadLoadDiagnostics()` returns a `LoadDiagnostics` snapshot
carrying the lifetime count of completed loads, how many retained reports the bound has evicted, and
the retained reports themselves, oldest completion first. `ClearLoadDiagnostics()` drains them and
leaves the lifetime counts alone.

Each `StateLoadReport` names the address, when the load started and finished, how long it took, the
same completeness value the `statesman.load.completeness` metadata key carries, how many declared
sources contributed and how many threw, and one `StateSourceLoadReport` per source in declaration
order — its name, whether it contributed, exactly how long it took, and the type and message of what
it threw when it did not. The exception recorded is the source's own, not the wrapper the loader adds.

Retention is the latest completed load per address, bounded at
`StatesmanDiagnostics.MaxRetainedLoadReports`, evicting the oldest completion first. A partitioned
state's address space is unbounded, so the bound is what keeps the surface from becoming a leak.

**A load that throws produces no report.** `RequireAll` with any source failure, and a best-effort load
where no source produced state and no initial value applied, both leave the loader before an outcome
exists and become a fault snapshot instead — counted on `statesman.faults` and carried on the
snapshot's own `StateError`. A `partial` or `initial-fallback` load completes and does report, which is
where the faulted-source detail lives.

## Telemetry

The core emits activities through `Statesman` and meters for reads, accepted commits, refreshes, faults, conflicts, operation duration, and maintenance failures (`statesman.maintenance.failures`, `statesman.maintenance.failures.suppressed`, and `statesman.maintenance.failures.dropped`). Tags include root, state path, partition, and operation. Avoid copying sensitive state values into tags.

See the [telemetry reference](telemetry.md) for the full instrument table, the exact tag keys, and the OpenTelemetry semantic-convention audit — which two names deviate from the conventions today, and why they are kept through the `0.x` line.

Pruning or cache-maintenance failure after an accepted cold append is reported as a maintenance failure and counted on `statesman.maintenance.failures`. It is not converted into a state fault because the authoritative state transition already succeeded. Retention of the failures themselves is rate-limited per ledger store and bounded overall — see the [observability guide](../operations/observability.md#maintenance-failures) — and `statesman.maintenance.failures.suppressed` and `statesman.maintenance.failures.dropped` count what the rate limit and the retention bound each discarded, respectively.

## Analyzer diagnostics

- `STM001`: a member of a `[ManagedState]` type is directly assigned outside an explicit boundary
- `STM002`: a `[ManagedState]` type exposes a public mutable field or setter
- `STM003`: `StateKey.Define<T>` receives a non-constant path and can destabilize the manifest

Use `[StateMutationBoundary]` for a documented reducer, importer, mapper, or legacy adapter. Use `[StateMutationAnalysisIgnore]` only at a narrow symbol with a concrete reason.
