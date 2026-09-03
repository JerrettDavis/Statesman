# ADR 0002: Providers expose a ledger contract, not a database abstraction

- Status: Accepted
- Date: 2026-09-02

## Context

A universal query API would either leak provider semantics or become an incomplete database. Statesman only needs the latest record, ordered history, conditional append, and retention.

## Decision

The minimum provider interface remains append-oriented and payload-opaque. Domain state is serialized by the runtime. Provider-specific capabilities use optional interfaces.

## Consequences

Storage is replaceable and domain code remains independent. Rich arbitrary queries belong in application projections or the underlying database, not in `IState<T>`.
