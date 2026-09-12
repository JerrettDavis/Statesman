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

The runtime counts provider pruning failures after successful writes, through `statesman.maintenance.failures` and `statesman.maintenance.failures.dropped` on the `Statesman` meter, and tiered stores expose their most recent cache error. Surface those counters through your normal metrics and health infrastructure. The runtime also retains the most recent exceptions for inspection in a debugger, bounded at 64 with everything past the bound counted as dropped — there is deliberately no API to read or clear that collection, so the counters are the supported signal. An accepted append remains accepted even when maintenance fails.
