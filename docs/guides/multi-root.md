# Multiple roots and container boundaries

A single process can host several Statesman roots. Each root has a distinct declaration, fingerprint, lifecycle, address namespace, active-handle cache, and root-level observation stream.

```csharp
services.AddStatesman(UserStateDeclaration.Build());
services.AddStatesman(InventoryStateDeclaration.Build());

IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
IStatesman users = registry.Get("users");
IStatesman inventory = registry.Get("inventory");
```

The unqualified `IStatesman` service is only available when exactly one root is registered. Multi-root applications should resolve `IStatesmanRegistry` so code cannot accidentally bind to an arbitrary authority.

## Three boundary strengths

An attached container is declaration organization plus a scoped runtime view. It is useful for a bounded context or feature area inside one authority.

An isolated container is still inside the root, but its intent is recorded in the manifest and it exposes scoped state access, capture, observation, and signals. It shares the root's lifecycle, provider resolver, and commit gate.

A separate root is the hard identity and lifecycle boundary. Assign different provider registrations when the roots must also have independent storage credentials, availability, retention operations, or blast radius. Roots may deliberately share a provider, but that is shared infrastructure, not physical disconnection.

## Choosing partitions versus containers versus roots

Use a partition for many independent instances of one definition, such as tenant, user, device, document, or deployment.

Use a container when several definitions form one named area and should be accessed or observed through a common path scope.

Use a root when a subsystem owns a separate composition root, startup policy, inspection surface, release lifecycle, or storage authority.
