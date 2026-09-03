# State lifecycle and read intent

A ledger stream begins absent. The first authoritative operation creates revision 1. Every accepted operation increments the state revision and receives a root-wide global position from the ledger provider.

## Status

| Status | Meaning |
|---|---|
| `Absent` | No revision exists in the ledger. |
| `Ready` | A value exists and is within its freshness window. |
| `Stale` | A value exists but its freshness window has elapsed. |
| `Invalidated` | A caller marked the value suspect. The previous value may remain available. |
| `Cleared` | A deliberate tombstone was recorded and no value is exposed. |
| `Faulted` | Acquisition failed. Policy determines whether the last-known value remains attached. |

Status is derived partly from the stored record and partly from observation time. A stored `Ready` record is observed as `Stale` after `FreshUntil` without requiring a write merely to mark time passing.

## Read modes

`Cached` returns the local in-process snapshot immediately and never hydrates or refreshes.

`Current` hydrates from the ledger. It applies the declaration's `OnFirstRead`, `WhenStale`, and stale-while-revalidate policies.

`RefreshIfStale` refreshes absent, stale, invalidated, or faulted state when a loader exists.

`Fresh` requires a fresh value and performs a foreground refresh when necessary. A faulted snapshot with a retained last-known value can still be returned when `AllowLastKnownOnFault` is enabled.

## Freshness

A successful value commit records both `FreshUntil` and `ServeUntil`. `FreshFor` controls authoritative freshness. `ServeStaleFor` records the intended stale-serving window for adapters and policy-aware callers. `StaleWhileRevalidate` allows a `Current` read to return an existing value and queue one single-flight background refresh.

The core runtime never invents a value after `ServeUntil`. It retains the timestamp in the snapshot so a host policy, endpoint, or future strict read mode can reject it. This avoids silently changing compatibility behavior for existing callers.

## Failure

Loader exceptions produce a `Faulted` ledger record with a structured `StateError`. With `KeepLastKnown`, the prior payload remains attached. With `ReplaceWithFault`, the fault record has no payload. Observers see the failure in either case.

Cancellation and optimistic-concurrency exceptions are not converted into loader faults.
