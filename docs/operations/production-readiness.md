# Production-readiness checklist

Before using Statesman as an application authority, review the declaration and deployment as one design.

## Authority and ownership

Confirm that each state has one owning root and that remaining legacy mutation paths are marked and scheduled for removal. Decide whether each boundary is a partition, container, or separate root. Use separate provider infrastructure when a root needs a separate blast radius.

## Acquisition and failure

Set timeouts and resilience policies in the services used by loaders. Choose require-all or best-effort intentionally. For best-effort composition, decide whether an initial fallback is semantically safe. Define freshness and last-known-value behavior from the consumer's risk, not only upstream polling cost.

## Persistence and recovery

Select retention from audit, privacy, storage, and recovery needs. Exercise backup and restore for the underlying provider. Validate serializer compatibility and every schema migration against production-like historical payloads. For tiered storage, choose cold validation or explicit hot preference based on the permitted replication lag.

## Concurrency

Use expected revisions for user edits or commands that must reject stale intent. Keep update functions and reducers side-effect free. Load-test high-contention partitions and monitor conflict retries.

## Security

Authorize inspection routes and signals. Protect provider credentials and filesystem permissions. Decide whether values require payload encryption. Scrub sensitive data from metadata, errors, traces, and logs.

## Delivery

Run the full cross-platform CI workflow, provider integration tests, package validation, and CodeQL. Pin deployment versions and retain the declaration manifest fingerprint with release artifacts. Treat fingerprint drift as an architectural change that deserves review.
