# Consistency and concurrency

Statesman uses explicit read intent and optimistic writes. It does not assign one vague word such as "current" to several incompatible guarantees.

## Read modes

`Cached` returns the handle's local observation without hydrating storage or invoking a loader. It is the only mode that intentionally permits an absent value simply because the handle has not been used yet.

`Current` hydrates the provider head and then follows declaration policy. It can refresh an absent value on first read and can refresh stale or invalidated state when configured. With stale-while-revalidate enabled, a usable stale value may be returned while one process-local refresh is queued.

`RefreshIfStale` waits for acquisition when the value is absent, stale, invalidated, or faulted.

`Fresh` waits for acquisition and only succeeds with a `Ready` value whose freshness window still covers the observation time. A retained last-known value from a fault may be returned only when `AllowLastKnownOnFault` is enabled. That exception remains visibly `Faulted`; it is never relabeled fresh.

## Revision compare-and-swap

Every accepted record has a stream revision. Runtime writes build a `StateWriteCondition` from the observed revision. A provider accepts the commit only when the head still matches.

`SetAsync` can retry an ordinary write after refreshing its local head. `UpdateAsync` and typed interaction reducers are re-evaluated against the new head. Because of that retry, update functions and reducers must be pure with respect to external systems.

Supplying `ExpectedRevision` disables implicit conflict recovery. A mismatch throws `StateConcurrencyException` with the expected and actual revision.

## Refresh coalescing

Refresh is single-flight per active state partition. Requests that arrive while the same partition is refreshing share the resulting observation instead of issuing duplicate source calls. Calls with an explicit expected revision retain their own compare-and-swap semantics and are not silently coalesced across revision intent.

## Capture

`CaptureAsync` hydrates the requested handles and then snapshots them under the root's process-local commit gate. This is coherent relative to commits made through the same `IStatesman` runtime. It is not a distributed snapshot and does not stop another process from appending to a shared provider.

## Provider ordering

A provider must guarantee strictly increasing revisions within a state address. `GlobalPosition` is monotonic within one provider authority and supports correlation or future cursor capabilities. It is not presented as a globally synchronized clock across unrelated stores.

Separate roots always have separate identities, manifests, active handles, and lifecycles. They can use different providers for a hard storage failure boundary or intentionally share one provider. Sharing a provider also shares that provider's operational fate and global-position authority.
