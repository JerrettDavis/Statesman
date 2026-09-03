# ADR 0006: Best-effort loads preserve degradation with the value

## Status

Accepted.

## Decision

When best-effort acquisition produces a usable value but one or more sources fail, Statesman appends a `Ready` value with a `partial-load` error and per-source metadata. When no source produces usable state, it appends a `Faulted` operation and applies the declared last-known-value policy.

## Consequences

Consumers can continue with a useful composition without losing the fact that it is incomplete. Operators can distinguish total outage from partial degradation. Snapshot consumers must not assume that `Ready` implies `Error` is null.
