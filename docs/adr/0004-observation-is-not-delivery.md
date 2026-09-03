# ADR 0004: In-process observation is not durable delivery

- Status: Accepted
- Date: 2026-09-02

## Context

State observers need low-overhead notifications, but pretending an in-process stream is a message broker creates false delivery guarantees.

## Decision

Observation uses bounded channels with drop-oldest backpressure. The ledger is authoritative. Lossless consumers must read history or use a future durable change-feed capability.

## Consequences

Slow observers cannot exhaust runtime memory. Consumers must choose explicitly between convenient live observation and durable processing.
