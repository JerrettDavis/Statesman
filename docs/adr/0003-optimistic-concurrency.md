# ADR 0003: State writes use optimistic revisions

- Status: Accepted
- Date: 2026-09-02

## Context

Silent last-write-wins behavior creates untraceable lost updates. A process-wide lock cannot coordinate distributed writers.

## Decision

Every append carries an expected revision or absent condition. Pure updates and reducers may retry against a new snapshot. Explicit expected revisions fail on conflict.

## Consequences

Providers must implement compare-and-swap semantics. Reducers must be side-effect free. Distributed coordination works without requiring one runtime process to be the sole writer.
