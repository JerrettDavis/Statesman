# Telemetry

Statesman emits an `ActivitySource` and a `Meter`, both named `Statesman`
(`StatesmanTelemetry.ActivitySourceName` and `StatesmanTelemetry.MeterName`). This page is the
OpenTelemetry semantic-convention audit for the meter: every instrument it publishes, the tag keys
every measurement carries, and the two places the shipped names deliberately deviate from the
[OpenTelemetry semantic conventions](https://opentelemetry.io/docs/specs/semconv/general/metrics/)
for now. `tests/Statesman.Tests/StatesmanTelemetryConventionTests.cs` pins every fact on this page —
a set-equality check, not a subset check — so an instrument added, renamed, or retyped without a
deliberate decision fails a test rather than silently drifting from what this page documents.

## Instruments

| Name | Kind | Unit | Meaning |
| --- | --- | --- | --- |
| `statesman.reads` | `Counter<long>` | (none) | A read of a state's current value. |
| `statesman.commits` | `Counter<long>` | (none) | A write accepted by the ledger. |
| `statesman.refreshes` | `Counter<long>` | (none) | A loader-driven refresh of a state's value. |
| `statesman.conflicts` | `Counter<long>` | (none) | A write rejected by an optimistic-concurrency check. |
| `statesman.faults` | `Counter<long>` | (none) | A state transitioning into a fault snapshot. |
| `statesman.maintenance.failures` | `Counter<long>` | (none) | Every maintenance failure reported, regardless of outcome. |
| `statesman.maintenance.failures.suppressed` | `Counter<long>` | (none) | Maintenance failures the per-source rate limit refused to retain, before the retention bound was ever consulted. |
| `statesman.maintenance.failures.dropped` | `Counter<long>` | (none) | Retained maintenance failures the retention bound later evicted, oldest first. |
| `statesman.operation.duration` | `Histogram<double>` | `ms` | Wall-clock duration of one state operation. |

See [Diagnostics and metadata](diagnostics.md#telemetry) for how the three maintenance-failure
counters relate to `IStatesmanDiagnostics.ReadMaintenanceFailures`, and the
[observability guide](../operations/observability.md#maintenance-failures) for the rate limit and
retention bound they report on.

## Tags

`StatesmanTelemetry.Tags(address, operation)` attaches exactly four keys to every measurement on a
per-state instrument (`statesman.reads`, `statesman.commits`, `statesman.refreshes`,
`statesman.conflicts`, `statesman.faults`, and `statesman.operation.duration`):

- `statesman.root` — the owning runtime's root id.
- `statesman.state` — the declared state's path.
- `statesman.partition` — the partition, `"default"` when none was given.
- `statesman.operation` — the operation name (for example `read`, `refresh`, or the write operation's
  name).

The three maintenance-failure counters carry no tags: a maintenance failure is attributed to a ledger
store, not a single state address, and the per-source rate limit already groups by store name
internally.

## Deviations from the OpenTelemetry semantic conventions

Two names here deviate from the conventions on purpose, and both are kept through the whole `0.x`
line:

- **`statesman.operation.duration` records milliseconds; the conventions ask for seconds with unit
  `s`.** Changing the unit on a shipped instrument changes what every already-recorded (and every
  future) value means under the same name — a backend cannot tell a `ms` sample from an `s` sample
  apart once both share one instrument name, so the change would shift interpretation silently rather
  than announce itself.
- **The counters carry no annotation unit, such as `{fault}` or `{failure}`.** An annotation unit is
  part of an instrument's identity for a backend that keys series on name-plus-unit, so adding one to
  an already-shipped counter is not additive — it is a new series under the same name, for no
  behavioural gain over the plain, unit-less counters shipped today.

**1.0 is where both are corrected.** `statesman.operation.duration` moves to seconds with unit `s`,
and each counter gains its annotation unit, as one breaking change documented on
[API compatibility](api-compatibility.md) with a migration note for whatever dashboards or alerts
key on the old unit. Until then, `StatesmanTelemetryConventionTests` is what makes any change to this
page's table a deliberate decision rather than an accident.
