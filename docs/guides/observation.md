# Observation and subscriptions

Observation is a projection of accepted ledger operations. Statesman has one event model: the ledger operation. Async streams and callback subscriptions are conveniences over that model rather than separate notification systems.

## Async streams

A typed handle exposes `ObserveAsync`, and a root or container exposes an untyped stream.

```csharp
await foreach (StateChange<CartState> change in cart.ObserveAsync())
{
    Console.WriteLine($"{change.Operation}: r{change.Current.Revision}");
}
```

`IncludeCurrent` emits each existing local record directly before buffered changes. An absent, never-recorded handle does not invent a synthetic ledger operation. `BufferCapacity` controls the bounded per-subscriber channel for subsequent notifications. Slow subscribers drop their oldest pending notification rather than applying backpressure to authoritative writes.

A container stream is filtered before changes enter its buffer. Traffic elsewhere in the root cannot crowd out notifications from the container being observed.

## Callback subscriptions

For `IOptionsMonitor`-style integration, the core package provides operation subscriptions:

```csharp
await using StateSubscription subscription = user.OnSet(
    async (change, cancellationToken) =>
    {
        await projection.RenderAsync(change.Current.RequiredValue, cancellationToken);
    });
```

Available helpers include `OnChange`, `On`, `OnSet`, `OnTransition`, `OnInvalidated`, `OnCleared`, and `OnFaulted`.

Callbacks execute outside the append critical section. A callback failure faults that subscription task; it does not reverse the accepted record. Keep callback work bounded and move durable processing to a queue or ledger cursor.

## Observation is not durable delivery

The bounded stream is intended for UI binding, cache invalidation, process-local coordination, diagnostics, and similar reactions. It is not a message broker. A process can stop, a subscriber can lag, and an old item can be dropped.

Consumers that cannot miss a revision should store a cursor and read ledger history. A provider-neutral durable change-feed capability is planned separately so its guarantees are not confused with process-local channels.
