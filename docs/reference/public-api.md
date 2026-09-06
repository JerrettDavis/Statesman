# Public API map

This page is a navigation aid rather than generated API documentation.

## Declaration

- `Statesman.Declare(id, version)` creates a single-use `StatesmanDeclarationBuilder`.
- `Defaults` declares inherited store, freshness, retention, refresh, fault, and equivalent-write behavior.
- `Container` declares attached or isolated path scopes with nested defaults and states.
- `State<T>` declares a singleton or partitioned typed state.
- `Initial`, `Load`, `Freshness`, `Refresh`, `Retain`, `Interact`, `Invariant`, `MigrateFrom`, `CompareWith`, `Describe`, and `Tag` complete a state definition.
- `Build` freezes runtime definitions, normalizes unordered values, emits a manifest, and computes its fingerprint.

## Runtime

- `IStatesman.State<T>` returns a typed state handle.
- `IStatesman.Container` returns a path-scoped view.
- `GetAsync`, `SetAsync`, `InvalidateAsync`, `ClearAsync`, `HistoryAsync`, `ObserveAsync`, and `CaptureAsync` support dynamic tooling and orchestration.
- `InitializeAsync`, `SignalAsync`, and `MaintainAsync` drive startup and proactive acquisition.
- `IStatesmanRegistry` resolves multiple named roots.

## State handle

- `Current` is a non-blocking local observation.
- `GetAsync` applies an explicit read mode.
- `SetAsync` records a supplied authoritative value.
- `UpdateAsync` computes a new value and may re-run after a conflict.
- `DispatchAsync` executes a declared typed interaction.
- `RefreshAsync` runs declared acquisition.
- `InvalidateAsync` retains a payload while marking it suspect.
- `ClearAsync` appends a tombstone.
- `HistoryAsync` reads retained revisions.
- `ObserveAsync` publishes accepted process-local changes.

## Snapshots

`IStateSnapshot` includes address, value type, stream revision, provider global position, observation and occurrence times, operation, status, value availability, freshness boundaries, error, and metadata. `IStateSnapshot<T>` adds the typed optional value and `RequiredValue`.

## Ledger

`IStateLedgerStore` has four responsibilities: read the head, read retained history, conditionally append, and prune. `IStateLedgerReplica` optionally imports exact authoritative records. `IStateStoreResolver` maps declaration store names to providers.

## Integration packages

Dependency injection registers named stores and roots. Hosting initializes and maintains roots. ASP.NET Core maps secured inspection endpoints. HTTP supplies JSON source helpers and a remote client. Testing supplies manual time, an isolated runtime harness, fluent scenario seeding, and portable snapshot fixtures for integration/E2E orchestration. Tooling exports one root's retained ledger history to a portable file and restores it exactly, refusing any export whose declaration fingerprint does not match the target's manifest. Outbox delivers ledger changes from the durable change feed to a message broker at least once, under a lease that keeps one dispatcher at a time advancing a persisted, monotonic cursor. Analyzers enforce managed-state mutation boundaries and deterministic keys.
