# Statesman architecture

## Problem statement

State management becomes difficult when a system has many partial authorities. A UI cache, service client, background poller, event handler, options monitor, database projection, and local singleton can all hold a version of the same concept. Flux-style systems solve this well when an application is designed around one store from the beginning, but they are expensive to introduce into large codebases whose components are loosely coupled and whose state comes from many services.

Statesman addresses the authority problem first. It does not require every caller to speak one dispatch protocol on day one. It creates one declared state boundary that can accept reactive values, actively load state, compose facets, constrain transitions, expose immutable snapshots, publish changes, and preserve lineage.

## Architectural thesis

The composition root is the declaration. The declaration is compiled into an immutable runtime definition and a deterministic manifest. Everything dynamic happens against that static truth.

```text
Declaration
    -> validation and normalization
    -> immutable manifest + runtime definitions
    -> runtime
        -> typed handles per state partition
        -> acquisition and transition coordination
        -> append-only ledger stores
        -> snapshots and observation streams
```

The system is split into three layers.

### Declaration

The declaration owns identity, policies, sources, interactions, invariants, schema migrations, and boundaries. It is deterministic and side-effect free until runtime creation.

### Runtime

The runtime owns active handles, hydration, single-flight refreshes, optimistic retry, local coherent capture, root and typed observation, startup warm-up, signals, and maintenance. It never knows Redis keys, EF entities, or filesystem paths.

### Ledger

A ledger provider owns durable ordering, conditional append, exact-revision import when supported, history, and pruning. It stores opaque payload bytes plus provider-independent metadata.

## Domain model

### State definition and state instance

A definition is static: `users/user<UserState>`, partitioned, stored in `shared`, refreshed from profile and roles sources.

An instance is dynamic: root `back-office`, path `users/user`, partition `user-42`. Each instance has its own revision stream.

### Snapshot

A snapshot is an immutable observation, not the state object itself. It separates the value's occurrence time from the caller's observation time. That distinction allows freshness to change without appending artificial timer records.

### Operation and status

Operation answers what happened. Status answers what can currently be said about availability. A `Faulted` operation may carry a retained value. An `Invalidated` operation deliberately keeps the prior payload while marking it suspect. A `Cleared` operation is a tombstone.

## Invariants

The implementation is designed around these invariants:

1. A declaration contains each state path at most once.
2. A state instance is uniquely identified by root, path, and partition.
3. Singleton definitions reject non-default partitions.
4. Accepted records have strictly increasing stream revisions.
5. Provider global positions are monotonic within one provider authority.
6. A commit is accepted only when its write condition matches the current head.
7. A typed handle can only be created for the type in the frozen definition.
8. State invariants are evaluated before payload serialization and append.
9. Observers are published only after an append succeeds.
10. Pruning cannot invalidate the accepted head.
11. Tiered storage imports exact authoritative records rather than appending replicas.
12. Manifest fingerprints are independent of dictionary and set iteration order.
13. A scoped container view cannot access a state outside its path.
14. Builder mutation is rejected after `Build()`.

## Concurrency model

Each active handle has a hydration gate and refresh gate. Hydration reads storage once unless a conflict replaces the local head. Refresh is single-flight per state partition.

Writes use provider compare-and-swap. `SetAsync` retries only when the caller did not require an explicit revision. `UpdateAsync` and interaction reducers are re-evaluated against the latest snapshot after conflicts. Because a reducer can run more than once, side effects inside reducers are invalid application behavior.

A root commit gate wraps provider append and in-process capture. This makes a capture coherent relative to commits made through the same runtime. It intentionally does not claim a cross-process snapshot guarantee.

## Acquisition model

Sources have two stages: fetch and apply. A complete-value source applies by replacement. A facet source applies through a declared projection.

Parallel mode runs fetches concurrently and applies successful results in declaration order. Sequential mode fetches and applies one source before moving to the next. Failure behavior is either require-all or best-effort. Best-effort composition records per-source diagnostics in snapshot metadata and marks the result as a partial load when at least one source succeeds; failure of every source becomes a fault rather than a deceptively successful empty composition.

An initial value gives composition a valid starting point. Without one, a facet source whose state type is a non-nullable reference can still only safely apply if its projection does not inspect the uninitialized current value. Declarations should normally provide an initial value for multi-facet composition.

## Freshness model

Freshness belongs to a successful value record. `FreshUntil` and `ServeUntil` use saturated timestamp addition so never-expiring policies do not overflow `DateTimeOffset`.

Read intent and declaration policy are separate. A declaration can allow on-first-read and stale refresh, while a caller can still request a cached observation or force freshness.

## Fault model

Acquisition failure is appended as state because it affects what observers and operators need to know. Cancellation is control flow and is not appended. Concurrency conflicts are coordination and are not appended as state faults.

A maintenance or pruning failure is reported separately because the authoritative append has already succeeded. It must not be converted into a false state transition.

## Storage model

The ledger contract is deliberately small:

```csharp
ReadLatestAsync(address)
ReadHistoryAsync(address, options)
AppendAsync(address, condition, commit)
PruneAsync(address, policy)
```

`IStateLedgerReplica` is optional. It imports an already authoritative record and is intended for hot caches, restores, and replication utilities.

The runtime serializes values through `IStateSerializer`. Providers never deserialize domain values. This lets a provider store mixed state types and schema versions without referencing application assemblies.

## Boundary model

A root is an identity and lifecycle authority. It becomes a separate physical storage authority only when it resolves to a distinct provider. A container is a path scope inside that root. An isolated container makes scope explicit to the manifest and runtime API, but it still shares the root's provider resolution and global position. A partition is an independent instance of one state definition.

This avoids overloading one mechanism. A tenant can be a partition, a bounded context can be a container, and an independently deployed subsystem can be a root.

## Observation model

Typed handles and roots publish bounded channel streams. Subscribers choose whether to receive the current local snapshot first. When a slow observer exceeds its buffer, the oldest pending notification is dropped. The ledger remains authoritative, so consumers that require lossless processing should persist a cursor and read history rather than treat in-process observation as a durable message queue.

This is an important non-goal: Statesman notifications are not a broker substitute.

## Analyzer model

Compile-time analysis enforces the intended direction without hiding migration exceptions. `[ManagedState]` identifies owned shapes. Direct assignments and mutable public surfaces are warnings. Explicit boundaries document where mutation remains intentional. Static key analysis protects manifest stability.

Analyzer limitations are documented rather than obscured. Aliased collection mutation, reflection, unsafe code, and external assemblies require code review or future data-flow analysis.

## Security model

The core runtime has no user identity or authorization opinion. Authorization belongs where state enters or leaves the trust boundary: application services, command requirements, endpoint policies, and provider credentials.

The optional endpoint package defaults to hidden values and disabled signals. Manifests still expose architecture and should be authorized in production. Correlation metadata must not be used as an untrusted authorization decision.

Payload encryption is a serializer concern. Provider transport and at-rest encryption remain provider configuration concerns.

## Extension points

Stable extension contracts include:

- `IStateLedgerStore`
- `IStateLedgerReplica`
- `IStateStoreResolver`
- `IStateSerializer`
- registered services used by loader delegates
- manifest export
- endpoint hosting and remote operations clients
- analyzers

Future capabilities should be modeled as optional interfaces instead of widening the minimum ledger contract. Examples include distributed coherent capture, native change feeds, leases, transactional multi-state append, server-side compaction, and partition discovery.

## Deliberate limitations of the first release

The first implementation does not claim cross-process atomic capture, multi-state transactions, durable subscription delivery, source-generated declarations, remote state transparency, automatic partition enumeration, or arbitrary state queries. These are distinct consistency and operational problems. Hiding them behind convenient methods would create guarantees the providers cannot uniformly honor.

The roadmap treats them as explicit capabilities with provider negotiation.

## Migration posture

Statesman can begin as a ledgered mirror. This is intentional. The analyzer boundary and source metadata make the temporary dual-system period visible. Over time, loaders become proactive ownership, reducers become transition ownership, and legacy mutable holders become adapters or disappear.

The architecture succeeds when callers no longer need to know where a state came from or where its history is stored, but can still inspect exactly how those decisions were declared.
