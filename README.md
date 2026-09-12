# Statesman

[![CI](https://github.com/JerrettDavis/Statesman/actions/workflows/ci.yml/badge.svg)](https://github.com/JerrettDavis/Statesman/actions/workflows/ci.yml)
[![Docs](https://github.com/JerrettDavis/Statesman/actions/workflows/docs.yml/badge.svg)](https://github.com/JerrettDavis/Statesman/actions/workflows/docs.yml)
[![codecov](https://codecov.io/gh/JerrettDavis/Statesman/graph/badge.svg)](https://codecov.io/gh/JerrettDavis/Statesman)
[![NuGet](https://img.shields.io/nuget/v/Statesman?logo=nuget)](https://www.nuget.org/packages/Statesman)

**Statesman is a declaration-driven state runtime and append-only state ledger for .NET.**

It gives large, loosely coupled systems a practical route to authoritative state without requiring a Flux or Redux rewrite. A Statesman composition root declares what state exists, where it comes from, how it may change, how fresh it must be, how long its history is retained, where that history is stored, and how the state is observed.

The declaration is executable architecture. At build time it becomes an immutable, deterministic manifest. At runtime it coordinates typed reads, reactive writes, proactive loading, reducers, freshness, optimistic concurrency, history, observation, and storage.

```csharp
StateKey<UserServiceState> user =
    StateKey.Define<UserServiceState>("users/user");

StatesmanDeclaration declaration = Statesman.Declare("back-office", "1.0")
    .Defaults(defaults => defaults
        .Freshness(freshness => freshness
            .FreshFor(TimeSpan.FromMinutes(5))
            .ServeStaleFor(TimeSpan.FromMinutes(30))
            .StaleWhileRevalidate())
        .Retain(history => history
            .Last(250)
            .For(TimeSpan.FromDays(30))))
    .Container("users", users => users
        .Isolated()
        .State(user, state => state
            .Partitioned()
            .Initial(new UserServiceState("", [], false))
            .Load(load => load
                .From<IUserApi, UserProfile>("profile",
                    (api, context, ct) =>
                        api.GetProfileAsync(context.Address.Partition.Value, ct))
                .Into((current, profile, _) => current with
                {
                    DisplayName = profile.DisplayName,
                })
                .From<IUserApi, IReadOnlyList<string>>("permissions",
                    (api, context, ct) =>
                        api.GetPermissionsAsync(context.Address.Partition.Value, ct))
                .Into((current, permissions, _) => current with
                {
                    Permissions = permissions,
                })
                .InParallel()
                .RequireAll())
            .Refresh(refresh => refresh
                .OnFirstRead()
                .WhenStale()
                .Every(TimeSpan.FromMinutes(10))
                .OnSignal("user.changed"))
            .Interact<SetEnabled>("set-enabled", interaction => interaction
                .Require((_, command) => command.ActorCanAdmin,
                    "The actor cannot administer users.")
                .Reduce((current, command) => current with
                {
                    Enabled = command.Enabled,
                }))))
    .Build();
```

```csharp
builder.Services.AddStatesman(declaration, statesman => statesman
    .UseFileSystemStore("local", "./state")
    .UseRedisStore("shared", connection));
builder.Services.AddStatesmanHosting();

IState<UserServiceState> ada = statesman.State(user, "user-42");
IStateSnapshot<UserServiceState> snapshot =
    await ada.GetAsync(StateReadOptions.Fresh);

await ada.DispatchAsync(new SetEnabled(
    Enabled: true,
    ActorCanAdmin: true));

await statesman.SignalAsync(new StateSignal(
    "user.changed",
    Partition: "user-42"));
```

## What Statesman is

Statesman is a higher-order state abstraction. It sits above storage, transport, polling, event delivery, caches, and domain services. It does not ask those systems to become Statesman-aware. They remain sources and sinks. Statesman owns the authoritative runtime view and records how that view changed.

A state has more than a value. It has a typed identity, partition, revision, global ledger position, status, freshness window, schema version, source, causation, metadata, error information, retention policy, and declared interaction surface.

## What Statesman is not

Statesman is not a database, distributed transaction coordinator, message broker, event-sourcing framework, entity tracker, or UI framework. A ledger provider may use a database or Redis, but Statesman does not expose their query models as its programming model. It deliberately keeps domain-facing code independent from the persistence mechanism.

## Core model

| Concept | Purpose |
|---|---|
| **Root** | A complete, independently registered state authority with its own manifest and lifecycle. Multiple roots may coexist in one process. |
| **Container** | A nested declaration and runtime scope. Attached containers organize a root. Isolated containers add scoped access, observation, capture, and signals. |
| **State key** | A static, typed identity such as `users/user`. |
| **Partition** | An independent instance of a state definition, such as one user, tenant, device, or document. |
| **Snapshot** | An immutable observation of a state revision at a particular read time. |
| **Operation** | A ledger fact: seeded, set, transitioned, refreshed, invalidated, cleared, faulted, or imported. |
| **Source** | A proactive loader that obtains a complete value or one facet of a composed value. |
| **Interaction** | A named, typed command plus requirements and reducer. |
| **Ledger store** | An append and history contract implemented by memory, files, Redis, EF Core, or a tiered hot/cold store. |
| **Manifest** | The deterministic output of the composition root, including a SHA-256 fingerprint. |

## Package map

| Package | Responsibility |
|---|---|
| `Statesman.Abstractions` | Identity types, snapshots, policies, manifests, runtime and ledger contracts. |
| `Statesman` | Fluent declaration DSL, runtime coordination, JSON serialization, in-memory ledger, manifest export. |
| `Statesman.Extensions.DependencyInjection` | DI registration, named stores, and multiple root registry. |
| `Statesman.Extensions.Hosting` | Warm-up and interval maintenance through `IHostedService`. |
| `Statesman.Persistence.FileSystem` | Atomic local JSON ledger. |
| `Statesman.Persistence.Redis` | Distributed optimistic ledger backed by Redis. |
| `Statesman.Persistence.EntityFrameworkCore` | Provider-neutral EF Core ledger model and store. |
| `Statesman.Persistence.EntityFrameworkCore.Sqlite` | Shipped SQLite migration for the EF Core ledger context. |
| `Statesman.Persistence.EntityFrameworkCore.SqlServer` | Shipped SQL Server migration for the EF Core ledger context. |
| `Statesman.Persistence.EntityFrameworkCore.PostgreSQL` | Shipped PostgreSQL migration for the EF Core ledger context. |
| `Statesman.Persistence.Tiered` | Cold authoritative store with an exact-revision hot replica. |
| `Statesman.Transport.Http` | HTTP JSON sources and a remote Statesman client. |
| `Statesman.AspNetCore` | Optional manifest, snapshot, history, and signal endpoints. |
| `Statesman.Analyzers` | Mutation-boundary and deterministic-key analyzers. |
| `Statesman.Testing` | Deterministic time, fluent state seeding, portable fixtures, test services, assertions, and a runtime harness. |
| `Statesman.Tooling` | Portable ledger export and fingerprint-validated exact restore. |
| `Statesman.Outbox` | At-least-once delivery of ledger changes from the durable change feed to a message broker, with a persisted monotonic cursor and lease-gated single dispatch. |
| `Statesman.Outbox.Redis` | Redis Streams delivery and Redis cursor storage for the outbox. |
| `Statesman.Outbox.EntityFrameworkCore` | Entity Framework Core cursor storage for the outbox, in its own opt-in `DbContext`. |
| `Statesman.Outbox.EntityFrameworkCore.Sqlite` | Shipped SQLite migration for the EF Core outbox cursor context. |
| `Statesman.Outbox.EntityFrameworkCore.SqlServer` | Shipped SQL Server migration for the EF Core outbox cursor context. |
| `Statesman.Outbox.EntityFrameworkCore.PostgreSQL` | Shipped PostgreSQL migration for the EF Core outbox cursor context. |

The core packages target .NET 8, 9, and 10. The eight Entity Framework Core packages and the samples target .NET 10.

## First run

```bash
dotnet restore Statesman.slnx
dotnet build Statesman.slnx --configuration Release --no-restore
dotnet test Statesman.slnx --configuration Release --no-build
dotnet run --project samples/Statesman.Sample.Console
```

The repository pins the .NET 10.0.303 SDK. See [Getting started](docs/getting-started.md) for the smallest useful declaration, [Architecture](docs/architecture/design.md) for the complete model, and the published [DocFX site](https://jerrettdavis.github.io/Statesman/) for browsable guides and API reference.

## Current implementation scope

This repository is an implementation-ready first release, not a mock scaffold. It includes the public contracts, fluent declaration system, runtime, in-memory and external providers, analyzers, host integrations, examples, tests, CI, packaging, CodeQL, Codecov coverage reporting, a DocFX site, and design documentation.

Run `eng/build.ps1` or `eng/build.sh` for the local release validation loop, or let the included GitHub Actions workflows perform restore, build, tests, coverage, package creation, docs publishing, and optional NuGet publishing.

## Design principles

1. **One declared authority, many migration paths.** Existing code can begin by pushing updates into Statesman. Loaders, reducers, immutable state values, and analyzers can follow incrementally.
2. **The declaration is inspectable.** Runtime behavior must be visible in a stable manifest instead of hidden across registration callbacks and conventions.
3. **History is a first-class consequence of state.** Every authoritative change has a revision and lineage, even when retention later prunes it.
4. **Storage is replaceable.** Domain code talks to state, never Redis keys, files, EF entities, or cache entries.
5. **Reads state their consistency intent.** Cached, current, refresh-if-stale, and fresh reads are explicit.
6. **Concurrency is never last-write-wins by accident.** Writes use expected revisions and retry pure updates or reducers against the newest snapshot.
7. **Failure is state.** Loader failures are observable ledger records and can retain a last-known value according to policy.
8. **Boundaries say what they guarantee.** A separate root is the hard identity and lifecycle boundary. Separate provider infrastructure makes it a storage failure boundary. Containers remain scopes within a root.

## Documentation

- [Getting started](docs/getting-started.md)
- [The declaration DSL](docs/concepts/declaration.md)
- [State lifecycle and reads](docs/concepts/lifecycle.md)
- [Containers, roots, and partitions](docs/concepts/boundaries.md)
- [Loaders and fan-in composition](docs/guides/loaders.md)
- [Interactions and reactive writes](docs/guides/interactions.md)
- [Migration guide](docs/guides/migration.md)
- [Ledger and retention](docs/concepts/ledger.md)
- [Storage providers](docs/providers/index.md)
- [ASP.NET Core and hosting](docs/guides/hosting.md)
- [Analyzers](docs/guides/analyzers.md)
- [Testing](docs/guides/testing.md) — inline seeds, portable snapshot fixtures, ASP.NET Core, Playwright, and distributed E2E orchestration
- [Observation and subscriptions](docs/guides/observation.md)
- [Multiple roots](docs/guides/multi-root.md)
- [Consistency and concurrency](docs/concepts/consistency.md)
- [Public API map](docs/reference/public-api.md)
- [Diagnostics](docs/reference/diagnostics.md)
- [Production readiness](docs/operations/production-readiness.md)
- [Repository release readiness](docs/operations/release-readiness.md)
- [Architecture and invariants](docs/architecture/design.md)
- [Implementation plan](docs/architecture/implementation-plan.md)
- [Roadmap](ROADMAP.md)

## License

MIT. See [LICENSE](LICENSE).
