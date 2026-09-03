# Analyzers

`Statesman.Analyzers` helps preserve one state authority while allowing deliberate migration boundaries.

## STM001: direct managed-state mutation

Reports assignments, compound assignments, increments, and decrements to fields or properties owned by a `[ManagedState]` type outside an allowed boundary.

Allowed locations include the managed type's constructor, object and `with` initializers, and symbols marked `[StateMutationBoundary]` or `[StateMutationAnalysisIgnore]`.

## STM002: publicly mutable managed-state shape

Reports public non-readonly fields and public non-init property setters on `[ManagedState]` types. Immutable records are the easiest compliant shape.

## STM003: dynamic state key

Reports `StateKey.Define<T>` calls whose path is not a compile-time string constant. Runtime partitions are the correct place for dynamic identity.

## Boundary attributes

`[StateMutationBoundary("reason")]` documents a reducer, serializer, mapper, or temporary legacy bridge that intentionally mutates a managed type.

`[StateMutationAnalysisIgnore("reason")]` is a narrower escape hatch. Keep the reason specific and configure code review to reject blanket suppression.

The analyzer cannot prove every alias, collection mutation, reflection write, unsafe write, or mutation performed inside an external assembly. Treat it as an architectural guardrail, not a runtime security mechanism.
