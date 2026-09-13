using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Statesman.Testing;

namespace Statesman.Hosting.Tests;

/// <summary>
/// Exercises <see cref="StatesmanHealthCheck"/> against real DI-registered runtimes rather than test
/// doubles, since the check's whole job is to read <see cref="IStatesmanRegistry"/> and
/// <see cref="IStatesmanDiagnostics"/> as a consumer's own container would expose them.
/// </summary>
public sealed class StatesmanHealthCheckTests
{
    private static readonly StateKey<int> Counter = StateKey.Define<int>("health-check/counter");

    [Fact]
    public async Task Healthy_when_every_root_is_initialized_with_no_retained_failures()
    {
        await using ServiceProvider provider = BuildProvider(services => services.AddStatesman(BuildDeclaration("healthy")));
        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
        await registry.Get("healthy").InitializeAsync();

        var check = new StatesmanHealthCheck(registry);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Unhealthy_before_initialization()
    {
        await using ServiceProvider provider = BuildProvider(services => services.AddStatesman(BuildDeclaration("uninitialized")));
        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();

        Assert.False(registry.Get("uninitialized").IsInitialized);

        var check = new StatesmanHealthCheck(registry);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Degraded_when_maintenance_failures_are_retained()
    {
        await using ServiceProvider provider = BuildProvider(services => services.AddStatesman(
            BuildDeclaration("degraded", storeName: "pruning"),
            builder => builder.UseStore("pruning", _ => new PruneFailingStore(new InMemoryStateLedgerStore("pruning")))));
        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
        IStatesman root = registry.Get("degraded");
        await root.InitializeAsync();
        await root.State(Counter).SetAsync(1);

        var check = new StatesmanHealthCheck(registry);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_never_drains_the_diagnostics_surface()
    {
        // The health check only reads the diagnostics surface; draining it (clearing retained
        // failures) is an operator action, not something a health probe should do as a side effect
        // of being polled.
        await using ServiceProvider provider = BuildProvider(services => services.AddStatesman(
            BuildDeclaration("readonly-check", storeName: "pruning-readonly"),
            builder => builder.UseStore("pruning-readonly", _ => new PruneFailingStore(new InMemoryStateLedgerStore("pruning-readonly")))));
        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
        IStatesman root = registry.Get("readonly-check");
        await root.InitializeAsync();
        await root.State(Counter).SetAsync(1);

        Assert.True(root.TryGetDiagnostics(out IStatesmanDiagnostics? diagnostics));
        int before = diagnostics.ReadMaintenanceFailures().Retained.Count;
        Assert.True(before > 0);

        var check = new StatesmanHealthCheck(registry);
        await check.CheckHealthAsync(new HealthCheckContext());

        int after = diagnostics.ReadMaintenanceFailures().Retained.Count;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Data_dictionary_carries_all_six_keys_with_the_expected_values()
    {
        await using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddStatesman(BuildDeclaration("data-uninitialized"));
            services.AddStatesman(
                BuildDeclaration("data-degraded", storeName: "pruning-data"),
                builder => builder.UseStore("pruning-data", _ => new PruneFailingStore(new InMemoryStateLedgerStore("pruning-data"))));
        });
        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
        IStatesman degraded = registry.Get("data-degraded");
        await degraded.InitializeAsync();
        await degraded.State(Counter).SetAsync(1);
        // "data-uninitialized" is left uninitialized deliberately, so the dictionary carries a
        // non-zero uninitialized count alongside a non-zero retained count.

        var check = new StatesmanHealthCheck(registry);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(2, result.Data["statesman.roots"]);
        Assert.Equal(1, result.Data["statesman.roots.uninitialized"]);
        Assert.Equal(1L, result.Data["statesman.maintenance.failures.retained"]);
        Assert.Equal(0L, result.Data["statesman.maintenance.failures.suppressed"]);
        Assert.Equal(0L, result.Data["statesman.maintenance.failures.dropped"]);
        Assert.Equal(0, result.Data["statesman.maintenance.stores.degraded"]);
    }

    [Fact]
    public async Task AddCheck_resolves_the_health_check_from_a_service_collection()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddStatesman(BuildDeclaration("resolved"));
        services.AddHealthChecks().AddCheck<StatesmanHealthCheck>("statesman");
        await using ServiceProvider provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IStatesmanRegistry>().Get("resolved").InitializeAsync();

        var healthCheckService = provider.GetRequiredService<HealthCheckService>();
        HealthReport report = await healthCheckService.CheckHealthAsync();

        Assert.True(report.Entries.ContainsKey("statesman"));
        Assert.Equal(HealthStatus.Healthy, report.Entries["statesman"].Status);
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider();
    }

    private static StatesmanDeclaration BuildDeclaration(string rootId, string storeName = "memory") =>
        global::Statesman.Statesman.Declare(rootId)
            .State(Counter, state => state
                .StoreWith(storeName)
                .Initial(0))
            .Build();

    /// <summary>
    /// Forwards everything to an inner store except <c>PruneAsync</c>, which always throws. Copied
    /// from <c>RuntimeMaintenanceFailureBoundTests</c> in <c>Statesman.Tests</c> rather than shared,
    /// since the two assemblies have no shared test helper.
    /// </summary>
    private sealed class PruneFailingStore : IStateLedgerStore
    {
        private readonly InMemoryStateLedgerStore _inner;

        public PruneFailingStore(InMemoryStateLedgerStore inner) => _inner = inner;

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(address, options, cancellationToken);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(address, condition, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("prune failure");

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
