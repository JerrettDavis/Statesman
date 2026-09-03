# Getting started

A useful Statesman setup has four pieces: a typed key, an immutable value, a declaration, and a runtime.

## 1. Define a static key and value

```csharp
using Statesman;

public static class ApplicationState
{
    public static readonly StateKey<SystemHealth> Health =
        StateKey.Define<SystemHealth>("system/health");
}

[ManagedState]
public sealed record SystemHealth(
    string Status,
    DateTimeOffset CheckedAt);
```

Keys should be stable and declared in one discoverable location. `Statesman.Analyzers` reports dynamically constructed keys because they prevent deterministic manifests and make state ownership difficult to inspect.

## 2. Declare behavior

```csharp
StatesmanDeclaration declaration = Statesman.Declare("operations")
    .State(ApplicationState.Health, state => state
        .Initial(new SystemHealth("unknown", DateTimeOffset.MinValue))
        .Freshness(freshness => freshness
            .FreshFor(TimeSpan.FromSeconds(30))
            .ServeStaleFor(TimeSpan.FromMinutes(5)))
        .Refresh(refresh => refresh
            .OnFirstRead()
            .WhenStale()
            .Every(TimeSpan.FromSeconds(30))
            .OnSignal("health.changed"))
        .Load(load => load.From<IHealthProbe>(
            "health-probe",
            (probe, _, cancellationToken) =>
                probe.CheckAsync(cancellationToken)))
        .Retain(retention => retention
            .Last(1_000)
            .For(TimeSpan.FromDays(14))))
    .Build();
```

`Build()` validates the declaration, freezes it, sorts all deterministic collections, and creates a manifest fingerprint. The builder cannot be reused after it has been built.

## 3. Register it

```csharp
builder.Services.AddSingleton<IHealthProbe, HealthProbe>();
builder.Services.AddStatesman(declaration);
builder.Services.AddStatesmanHosting();
```

The DI package always provides a named `memory` store. Other providers can be registered by name and selected globally or per state.

## 4. Read and change state

```csharp
IState<SystemHealth> health = statesman.State(ApplicationState.Health);

IStateSnapshot<SystemHealth> snapshot =
    await health.GetAsync(StateReadOptions.Fresh, cancellationToken);

Console.WriteLine(snapshot.RequiredValue.Status);
Console.WriteLine(snapshot.Revision);

await health.InvalidateAsync("operator requested a new probe");
await statesman.SignalAsync(new StateSignal("health.changed"));
```

`Current` is a synchronous, immutable local observation. `GetAsync` can hydrate from storage and refresh according to the requested read mode. `RequiredValue` throws `StateUnavailableException` when no usable value exists; `Value` and `HasValue` allow explicit absence handling.

## Multiple roots

Call `AddStatesman` more than once to host independent state authorities in one process:

```csharp
services.AddStatesman(customerDeclaration);
services.AddStatesman(machineDeclaration);

IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
IStatesman customers = registry.Get("customers");
IStatesman machines = registry.Get("machines");
```

Use separate roots when state must have an independent manifest, lifecycle, ownership, or store topology. Assign separate providers when storage availability must also be independent. Use containers and partitions for scopes inside a root.
