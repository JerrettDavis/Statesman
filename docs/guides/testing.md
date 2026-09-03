# Testing Statesman applications

`Statesman.Testing` is designed around one rule: a test should be able to describe **what the world looks like before the action** without reproducing all of the application's orchestration code.

Statesman therefore supports three complementary test styles:

1. **Inline seeding** for concise Given/When/Then tests.
2. **Portable fixtures** for repeatable integration and E2E scenarios.
3. **Normal loaders and interactions** when the behavior under test is the acquisition or transition itself.

Seeding uses Statesman's normal mutation path. It does not reach around the runtime and mutate objects directly, so invariants, revisions, ledger records, observers, and metadata remain active.

## Inline scenario seeding

```csharp
await using StatesmanTestHarness harness = StatesmanTestHarness.Create(declaration);

await harness.Seed()
    .Source("scenario:admin-with-cart")
    .Metadata("case", "checkout-001")
    .State(UserKeys.Current, new UserState("user-42", IsAdmin: true))
    .State(CartKeys.Current, new CartState(["sku-1", "sku-2"]))
    .Invalidated(PricingKeys.Current, stalePricing)
    .ApplyAsync();

// Act against the real application behavior.
await checkout.ExecuteAsync();

// Assert from the authoritative state.
harness.Runtime.State(CartKeys.Current).Current.ShouldBeReady();
```

`StateSeedBuilder` also supports partitioned state:

```csharp
await harness.Seed()
    .State(UserKeys.User, alice, new StatePartition("alice"))
    .State(UserKeys.User, bob, new StatePartition("bob"))
    .ApplyAsync();
```

This makes scenario setup explicit while avoiding dozens of service mocks whose only purpose is to manufacture a starting state.

## Snapshot a scenario into a fixture

Any coordinated `StateSnapshotSet` can become a portable JSON fixture:

```csharp
StateFixture fixture = await statesman.CaptureFixtureAsync(new[]
{
    UserKeys.Current.At(StatePartition.Default),
    CartKeys.Current.At(StatePartition.Default),
    FeatureKeys.Current.At(StatePartition.Default),
});

await fixture.SaveAsync("fixtures/member-with-cart.statesman.json");
```

The fixture records:

- Statesman root and manifest fingerprint
- state path and partition
- CLR value identity
- logical state status
- serialized value
- state metadata
- fixture metadata

A fixture intentionally does **not** claim that imported revisions happened in the new test process. Applying it creates new local ledger revisions with `Source = "test-fixture"`.

That distinction makes fixtures deterministic without falsifying ledger provenance.

## Seed another run from the fixture

```csharp
StateFixture fixture = await StateFixtureExtensions.LoadFixtureAsync(
    "fixtures/member-with-cart.statesman.json");

await harness.ApplyFixtureAsync(fixture);
```

By default Statesman verifies both the root name and manifest fingerprint. A fixture created against a materially different state declaration therefore fails early instead of silently producing a misleading test.

During staged migrations these checks can be relaxed explicitly:

```csharp
await statesman.ApplyFixtureAsync(
    fixture,
    new StateFixtureApplyOptions
    {
        RequireMatchingManifest = false,
        FailOnUnknownState = false,
    });
```

Use relaxed matching as a migration tool, not as the permanent default.

## Integration tests

For service-level integration tests, create the real declaration and replace only true external boundaries:

```csharp
await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
    declaration,
    services => services
        .Add<IClockService>(fakeClockService)
        .Add<IPaymentGateway>(fakeGateway));

await harness.Seed()
    .State(AccountKeys.Current, account)
    .State(OrderKeys.Current, pendingOrder)
    .ApplyAsync();

await sut.CapturePaymentAsync();

harness.Runtime.State(OrderKeys.Current)
    .Current
    .ShouldEqualValue(expectedOrder);
```

The point is not to mock Statesman. Statesman is the integration seam. Seed the state authority and let the system under test interact with it normally.

## ASP.NET Core and TestServer

Seed the root before the first HTTP request:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.WebHost.UseTestServer();
builder.Services.AddStatesman(declaration);

await using WebApplication app = builder.Build();
IStatesman statesman = app.Services.GetRequiredService<IStatesman>();

await statesman.Seed()
    .State(SessionKeys.User, testUser)
    .State(FeatureKeys.Current, testFeatures)
    .ApplyAsync();

await app.StartAsync();
HttpClient client = app.GetTestClient();

HttpResponseMessage response = await client.GetAsync("/checkout");
```

This is particularly useful when endpoint tests previously required constructing a large graph of fake repositories and fake API responses just to reach the desired application condition.

## WebApplicationFactory

With `WebApplicationFactory<TEntryPoint>`, seed after the host is constructed and before the browser or client performs the scenario:

```csharp
await using WebApplicationFactory<Program> factory = new();
IStatesman statesman = factory.Services.GetRequiredService<IStatesman>();

await statesman.Seed()
    .State(SessionKeys.User, testUser)
    .State(NotificationKeys.Current, notifications)
    .ApplyAsync();

HttpClient client = factory.CreateClient();
```

For suites that create one host per case, fixture files make each case independently reproducible.

## Browser E2E with Playwright

A useful Playwright lifecycle is:

```text
fixture JSON
    ↓
start application in Testing environment
    ↓
seed Statesman before accepting traffic
    ↓
start Playwright scenario
    ↓
interact through public UI/API
    ↓
capture/assert final state
```

The application can opt into fixture loading only in its test bootstrap:

```csharp
if (app.Environment.IsEnvironment("Testing") &&
    Environment.GetEnvironmentVariable("STATESMAN_FIXTURE") is { } fixturePath)
{
    StateFixture fixture = await StateFixtureExtensions.LoadFixtureAsync(fixturePath);
    await app.Services.GetRequiredService<IStatesman>().ApplyFixtureAsync(fixture);
}
```

Then an E2E runner can launch the application with a case-specific fixture:

```text
STATESMAN_FIXTURE=fixtures/checkout/expired-card.statesman.json
```

The browser still interacts through the application's public surface. The fixture controls only the deterministic starting condition.

## Container and compose orchestration

Fixtures also work well when an E2E environment is composed from multiple processes.

For example:

```text
fixtures/order-cancelled/
├── identity.statesman.json
├── catalog.statesman.json
├── orders.statesman.json
└── payments.statesman.json
```

Each service owns its own Statesman root and consumes only its own fixture at startup.

A compose/test harness can mount the fixture directory and assign each path through environment variables. This produces a declarative distributed scenario without a central test script reaching into every service database.

The service remains responsible for interpreting its state declaration. The orchestrator merely says, "start this root from this declared logical state."

## Testing event-driven systems

Seed the initial state, subscribe to changes, then deliver the real event:

```csharp
await harness.Seed()
    .State(DeviceKeys.Status, DeviceStatus.Offline)
    .ApplyAsync();

IReadOnlyList<StateChange<DeviceStatus>> changes =
    await harness.CollectChangesAsync(
        harness.Runtime.State(DeviceKeys.Status),
        async () => await messageHandler.HandleAsync(deviceOnline),
        expected: 1);
```

This cleanly separates:

- fixture setup
- real message handling
- state transition
- observer verification

## Testing freshness and time

`ManualTimeProvider` starts at a known instant and can advance without sleeping. Use it to test freshness, stale reads, interval maintenance, retention age, and timestamps.

```csharp
await harness.Seed()
    .State(PriceKeys.Current, price)
    .ApplyAsync();

harness.Time.Advance(TimeSpan.FromMinutes(11));
await harness.Runtime.MaintainAsync();
```

## Golden fixtures

A fixture can be checked into source control as a human-readable test asset.

Good fixture candidates are meaningful domain scenarios:

```text
anonymous-user.statesman.json
admin-user.statesman.json
cart-with-backorder.statesman.json
printer-offline.statesman.json
subscription-grace-period.statesman.json
```

Avoid creating a fixture for every incidental test. Inline `Seed()` remains clearer for small cases.

Golden fixtures are particularly useful when the same scenario must be consumed by:

- unit/integration tests
- API tests
- Playwright tests
- smoke tests
- local developer reproduction
- CI orchestration

## Fixture seeding versus ledger replay

These are intentionally different operations.

**Fixture seeding** says:

> Establish this logical starting condition in a new runtime.

**Ledger replay** says:

> Reproduce this historical sequence with its original revision semantics.

`StateFixture` implements the first model. Applying a fixture creates new ledger entries and preserves provenance as test-generated state. This is the appropriate default for integration and E2E tests.

True ledger replay belongs at the persistence/provider layer and should be used for migration tests, store compatibility tests, forensic reproduction, and event/history-sensitive scenarios.

## Anti-pattern: a production test mutation endpoint

Do not expose a general `POST /state/seed` endpoint in normal application hosting merely to make E2E tests convenient.

Prefer one of these boundaries:

1. In-process seeding through the test host.
2. Startup fixture loading gated by a dedicated `Testing` environment.
3. A separately compiled test host or test-only startup assembly.
4. Provider-level isolated test infrastructure.

The goal is easy orchestration without turning test convenience into a production mutation surface.

## What to test

Test declarations as architecture and runtime behavior separately.

Declaration tests should cover duplicate keys and source names, manifest fingerprint stability, inherited defaults, container ownership, migration versions, and invalid policies.

Runtime tests should cover read modes, loader composition, optimistic conflicts, reducer retries, failure behavior, history order, retention, observation, signals, fixture compatibility, and partition isolation.

Provider contract tests should run the same append, conflict, ordering, import, and retention cases against every ledger implementation.
