# Roots, containers, and partitions

Statesman provides three different kinds of boundary. They solve different problems and should not be treated as synonyms.

## Root

A root is a complete state authority. It has one declaration, manifest, runtime lifecycle, observation stream, and store resolver. Separate roots are the strongest identity and lifecycle boundary. They have independent storage only when configured with independent provider instances or infrastructure.

Use separate roots when two state spaces:

- are owned by different subsystems
- must initialize and fail independently
- need different serializer or store resolution
- should not share root-level capture or signals
- may be deployed or versioned separately

The DI registry allows many roots in one application.

## Attached container

An attached container organizes state under a path and inherits declaration defaults. It is useful for ownership, documentation, tags, scoped access, and nested composition.

```csharp
.Container("users", users => users
    .Container("preferences", preferences => preferences
        .State<PreferenceState>("theme", state => ...)))
```

## Isolated container

An isolated container remains part of the root ledger but advertises a stronger logical boundary. `IStateContainer` provides scoped state access, history, capture, observation, and signal dispatch. It rejects attempts to reach state outside its path.

```csharp
IStateContainer users = statesman.Container("users");
IState<UserState> ada = users.State(UserKeys.User, "ada");
```

Isolation inside one root is not a distributed transaction or security boundary. Authorization still belongs at the host and endpoint layers. Use a separate root or process when ownership must be physically disconnected.

## Partition

A partition creates independent revision streams from one static definition. The key remains stable while the partition identifies a tenant, user, device, document, environment, or other runtime instance.

```csharp
StateKey<UserState> user = StateKey.Define<UserState>("users/user");

.State(user, state => state.Partitioned())

IState<UserState> ada = statesman.State(user, "ada");
IState<UserState> grace = statesman.State(user, "grace");
```

Singleton state only accepts the `default` partition. Statesman rejects accidental non-default partitions for singleton definitions.

## Choosing a boundary

Use a partition when behavior and policy are identical but identity varies. Use a container when related state should share declaration defaults and a scoped runtime view. Use a root when lifecycle, ownership, or release identity must be independent. Give those roots different provider infrastructure when operational failure must also be independent.
