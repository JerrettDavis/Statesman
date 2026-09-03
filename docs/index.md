# Statesman documentation

Statesman makes the composition root the authoritative declaration of application state. The runtime created from that declaration handles acquisition, transitions, observation, history, freshness, and persistence without binding callers to one UI architecture or storage system.

Start with [Getting started](getting-started.md). The [architecture document](architecture/design.md) explains the model and invariants, while the [implementation plan](architecture/implementation-plan.md) maps the path from this first vertical slice to a stable distributed-capability platform.

## Read by task

- I need to adopt Statesman in an existing application: [Migration guide](guides/migration.md)
- I need to combine several endpoints into one state: [Loaders](guides/loaders.md)
- I need controlled typed transitions: [Interactions](guides/interactions.md)
- I need independent tenants, users, devices, or documents: [Boundaries and partitions](concepts/boundaries.md)
- I need several authorities in one process: [Multiple roots](guides/multi-root.md)
- I need to understand fresh, current, cached, and conflict behavior: [Consistency](concepts/consistency.md)
- I need durable history: [Ledger](concepts/ledger.md) and [providers](providers/index.md)
- I need process-local subscriptions: [Observation](guides/observation.md)
- I need hosted polling and secure inspection: [Hosting](guides/hosting.md)
- I need architectural enforcement: [Analyzers](guides/analyzers.md)
- I need deterministic tests: [Testing](guides/testing.md)
- I need API or diagnostic details: [Public API](reference/public-api.md), [manifest](reference/manifest.md), and [diagnostics](reference/diagnostics.md)
- I am preparing a production deployment: [Observability](operations/observability.md) and [production readiness](operations/production-readiness.md)
- I need the repository and release automation checklist: [Repository release readiness](operations/release-readiness.md)

See the [glossary](glossary.md), [ADRs](adr/0001-declaration-is-authority.md), and repository [roadmap](../ROADMAP.md) for vocabulary and design history.
