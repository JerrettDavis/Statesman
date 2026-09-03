# Migrating an existing codebase

Statesman is designed for staged adoption. The safe path is to establish observation and lineage first, then move ownership.

## Phase 1: Inventory state

Identify state that is copied, cached, refreshed, invalidated, or independently mutated in several places. Give each concept one static `StateKey<T>`. Do not start by converting every DTO or local variable.

## Phase 2: Mirror legacy updates

Declare state with no proactive loader and push values from existing change events, polling jobs, or setters.

```csharp
[StateMutationBoundary("Legacy bridge until session ownership moves to Statesman.")]
public sealed class LegacySessionBridge
{
    public ValueTask PublishAsync(LegacySession legacy) =>
        _state.SetAsync(
            new SessionState(legacy.UserId, legacy.IsAuthenticated),
            new StateWriteOptions { Source = "legacy-session" });
}
```

Consumers can begin reading `IState<T>` while the legacy system remains the source. This immediately creates snapshots, observation, history, and diagnostics.

## Phase 3: Replace duplicate caches

Move existing acquisition code into declared loaders. Keep the old API surface as an adapter over `IState<T>` so callers do not all change at once.

## Phase 4: Move transitions

Replace ad hoc mutation with `UpdateAsync` or typed interactions. Prefer immutable records. Add invariants at the state boundary.

## Phase 5: Turn on analyzers

Install `Statesman.Analyzers` as warnings first. Mark authoritative state types with `[ManagedState]`. Add explicit mutation boundaries only around intentional adapters. Ratchet `STM001` and `STM003` to errors in CI once the migration surface is understood.

```ini
[*.cs]
dotnet_diagnostic.STM001.severity = error
dotnet_diagnostic.STM002.severity = warning
dotnet_diagnostic.STM003.severity = error
```

## Phase 6: Move durability

Start in memory. Select filesystem storage for local applications, Redis for a shared low-latency authority, EF Core for relational durability, or a tiered store when hot and cold responsibilities differ.

## Avoid the dual-authority trap

A mirror is intentionally temporary. Document which side is authoritative, tag mirrored writes with a source, and give the bridge an exit condition. Two components that both write independently are not migration, they are competing state authorities.
