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

Application metadata is merged first and reserved loader metadata wins, preventing a caller from falsely reporting loader health.

## Telemetry

The core emits activities through `Statesman` and meters for reads, accepted commits, refreshes, faults, conflicts, operation duration, and maintenance failures (`statesman.maintenance.failures`, `statesman.maintenance.failures.suppressed`, and `statesman.maintenance.failures.dropped`). Tags include root, state path, partition, and operation. Avoid copying sensitive state values into tags.

See the [telemetry reference](telemetry.md) for the full instrument table, the exact tag keys, and the OpenTelemetry semantic-convention audit — which two names deviate from the conventions today, and why they are kept through the `0.x` line.

Pruning or cache-maintenance failure after an accepted cold append is reported as a maintenance failure and counted on `statesman.maintenance.failures`. It is not converted into a state fault because the authoritative state transition already succeeded. Retention of the failures themselves is rate-limited per ledger store and bounded overall — see the [observability guide](../operations/observability.md#maintenance-failures) — and `statesman.maintenance.failures.suppressed` and `statesman.maintenance.failures.dropped` count what the rate limit and the retention bound each discarded, respectively.

## Analyzer diagnostics

- `STM001`: a member of a `[ManagedState]` type is directly assigned outside an explicit boundary
- `STM002`: a `[ManagedState]` type exposes a public mutable field or setter
- `STM003`: `StateKey.Define<T>` receives a non-constant path and can destabilize the manifest

Use `[StateMutationBoundary]` for a documented reducer, importer, mapper, or legacy adapter. Use `[StateMutationAnalysisIgnore]` only at a narrow symbol with a concrete reason.
