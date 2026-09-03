# Implementation plan and capability map

This document records how Statesman grows from the first repository release into a mature state platform while preserving a small, trustworthy core.

## Product objective

Statesman should let a team declare authoritative state once and adopt that authority incrementally across a large codebase. The declaration must answer five questions without reading runtime implementation code:

1. What state exists and where is its boundary?
2. How is it acquired, refreshed, and degraded?
3. Which interactions may change it?
4. What lineage is retained and where?
5. Which consistency guarantee does each read or write request?

## Package architecture

`Statesman.Abstractions` carries identities and contracts with no provider dependency. `Statesman` freezes declarations and coordinates state. Integration packages adapt DI, hosting, HTTP, ASP.NET Core, analyzers, and tests. Persistence packages own physical storage models. No provider entity or transport DTO enters a domain-facing state API.

## Workstreams

### Declaration and tooling

Keep the fluent API readable as a sentence, then normalize it into a deterministic manifest. Add compatibility baselines before 1.0. Future generated accessors and source-generated manifests must consume the same frozen model rather than create a parallel declaration path.

### Runtime correctness

Prove hydration, refresh coalescing, optimistic retry, fault recording, stale reads, observation, and disposal under race. Add stress and model-based tests around revisions. Multi-state transactions remain absent until a capability can state atomicity across specific providers.

### Provider conformance

Extract a shared contract suite. Each provider must prove the same head, append, history, import, and prune behavior. Provider-native acceleration is allowed only when fallback semantics remain equivalent or the stronger behavior is exposed through a separate interface.

### Distributed capability negotiation

Add optional interfaces for change feeds, leases, partition catalogs, and coherent capture. Runtime APIs should report unsupported capabilities rather than pretend a process-local implementation is distributed.

### Enforcement and migration

Expand analyzers from direct assignments into collection mutation and data flow. Supply code fixes for immutable shapes and state-handle injection. Preserve explicit mutation boundaries so teams can migrate subsystem by subsystem.

### Operations and security

Publish semantic telemetry, health checks, endpoint policies, corruption behavior, backup guidance, and compatibility procedures. Never include values in metrics by default. Make maintenance degradation observable without rewriting accepted state history.

## Quality gates

Every public behavior needs an executable specification. Declaration tests prove validation and fingerprints. Runtime tests prove lifecycle and races. Provider tests use a common suite. Host and transport behavior uses end-to-end tests. Analyzer behavior uses compiled snippets. Release CI builds all target frameworks, produces symbols, validates packages, scans code, and records checksums.

## Release sequence

The 0.1 line establishes vocabulary and a complete vertical slice. The 0.2 line hardens contracts and operational behavior. The 0.3 line introduces explicit distributed capabilities. The 0.4 line adds derived-state graphs and generated ergonomics. The 1.0 release freezes the authority contract after compatibility, performance, recovery, and production evidence gates are met. See the repository [roadmap](../../ROADMAP.md) for milestone exit criteria.
