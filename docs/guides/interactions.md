# Reactive writes and typed interactions

Reactive code tells Statesman what changed. It can push a complete value, derive a new value from the current snapshot, or dispatch a typed command to a declared interaction.

## Set

```csharp
await state.SetAsync(new CartState(lines), new StateWriteOptions
{
    Source = "checkout-ui",
    CorrelationId = requestId,
});
```

Use `SetAsync` at migration boundaries and when an external authority already produced the full canonical value.

## Update

```csharp
await state.UpdateAsync(current => (current ?? CartState.Empty) with
{
    LastViewedAt = clock.GetUtcNow(),
});
```

Statesman retries the function after optimistic conflicts. The update must therefore be pure and repeatable.

## Interaction

Interactions put accepted commands and transition rules in the declaration:

```csharp
.Interact<AddLine>("add-line", interaction => interaction
    .Describe("Adds a purchasable line to the cart.")
    .Require((cart, command) => command.Quantity > 0,
        "Quantity must be positive.")
    .Require(async (cart, command, context, cancellationToken) =>
        await policy.CanAddAsync(cart, command, cancellationToken)
            ? null
            : "The line cannot be added.")
    .Reduce((cart, command) => cart.Add(command.Sku, command.Quantity)))
```

```csharp
await cart.DispatchAsync(new AddLine("SKU-42", 2));
```

Requirements are collected and returned together through `StateInteractionRejectedException`. The reducer runs only after all requirements pass. State invariants run on the reduced value before commit.

When one command type has several declared interactions, pass the interaction name explicitly. When exactly one interaction accepts the command type, Statesman resolves it automatically.

## Equivalent writes

`.SuppressEquivalentWrites()` returns the current snapshot without appending when the declared equality comparer considers the new value equivalent. The default is `EqualityComparer<T>.Default`; `.CompareWith(...)` can provide domain-specific equality.

## Mutation analyzers

Mark managed state values with `[ManagedState]`. The analyzer reports public mutable members and direct mutation outside constructors, object initializers, `[StateMutationBoundary]`, or a narrowly justified ignore attribute.
