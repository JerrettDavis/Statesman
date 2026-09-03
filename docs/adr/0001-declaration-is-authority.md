# ADR 0001: The composition root is the state declaration

- Status: Accepted
- Date: 2026-09-02

## Context

State behavior distributed across DI registration, pollers, UI stores, cache wrappers, and event handlers cannot be inspected as one architecture. Migration into a mandatory global dispatch model is too disruptive for many existing systems.

## Decision

Statesman uses one fluent declaration per root. The declaration freezes identity, lifecycle, acquisition, transition, storage, and retention policy into runtime definitions and a deterministic manifest.

## Consequences

Behavior becomes inspectable and testable. Dynamic state identity must use partitions instead of dynamic key declarations. Executable delegates cannot be fully represented in the manifest, so names and static type metadata are part of the public contract.
