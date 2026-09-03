# Proactive loading and fan-in composition

A loader lets Statesman actively acquire state. The source can be an HTTP client, database reader, file parser, assembly scanner, device client, process probe, or any registered service. Statesman depends only on the declared delegate.

## Complete-value source

```csharp
.Load(load => load.From<IUserApi>(
    "user",
    (api, context, cancellationToken) =>
        api.GetAsync(context.Address.Partition.Value, cancellationToken)))
```

## Facet composition

A state often represents a useful application view that no single endpoint returns. A source can fetch a facet and apply it to the working value.

```csharp
.Initial(UserState.Empty)
.Load(load => load
    .From<IUserApi, Profile>("profile", (api, context, ct) =>
        api.GetProfileAsync(context.Address.Partition.Value, ct))
    .Into((state, profile, _) => state with
    {
        Name = profile.Name,
        Email = profile.Email,
    })
    .From<IAuthorizationApi, IReadOnlyList<Role>>("roles", (api, context, ct) =>
        api.GetRolesAsync(context.Address.Partition.Value, ct))
    .Into((state, roles, _) => state with { Roles = roles })
    .InParallel()
    .RequireAll())
```

Fetches run in parallel, but facet application always follows declaration order. That keeps final composition deterministic even when endpoint completion order varies.

## Sequential sources

Use `Sequentially()` when a later source must observe effects from an earlier application step, or when the upstream system must not receive concurrent requests. Each source is fetched and applied before the next begins.

## Failure modes

`RequireAll()` faults the refresh when any source fails.

`BestEffort()` skips failed facets. It succeeds when an initial value remains usable or at least one source produces a value. A partial success is appended as a `Ready` snapshot with a `partial-load` error and reserved `statesman.source.<name>.status` metadata. This keeps the usable value and its degraded provenance together.

When every source fails and no initial value was applied, acquisition appends a `Faulted` record. A prior value may be retained according to `KeepLastKnownOnFault`, but it is not described as a successful partial refresh.

## Context

`StateLoadContext` supplies the service provider, full state address, current immutable snapshot, and `TimeProvider`. Partition-aware loaders should use `context.Address.Partition` instead of capturing dynamic keys.

## Refresh triggers

A loader can be invoked explicitly with `RefreshAsync`, by read intent, at startup, on a hosted interval, or by a named signal. Refreshes are single-flight per state partition.
