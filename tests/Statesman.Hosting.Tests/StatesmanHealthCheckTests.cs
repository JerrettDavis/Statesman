using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Statesman.TestHelpers;
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
    public async Task Data_dictionary_carries_every_key_with_the_expected_values()
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
        Assert.Equal(0, result.Data["statesman.load.reports"]);
        Assert.Equal(0, result.Data["statesman.load.reports.incomplete"]);
        Assert.Equal(0L, result.Data["statesman.load.sources.faulted"]);
        Assert.Equal(string.Empty, result.Data["statesman.load.slowest.source"]);
        Assert.Equal(0d, result.Data["statesman.load.slowest.duration.ms"]);
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

    private static readonly StateKey<int> Loaded = StateKey.Define<int>("health-check/loaded");

    [Fact]
    public async Task Degraded_when_a_states_latest_load_did_not_complete()
    {
        // ROADMAP 0.3 pre-Phase-17 addendum decision 71: a partial load is degraded, not unhealthy —
        // the state is usable and authoritative, it is just not what the declaration asked for.
        var clock = new ManualTimeProvider();
        var source = new FlakySource(clock);
        await using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(source);
            services.AddStatesman(BuildLoadingDeclaration("load-degraded"));
        });
        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
        IStatesman root = registry.Get("load-degraded");
        await root.InitializeAsync();
        source.Fail = true;
        _ = await root.State(Loaded).RefreshAsync();

        var check = new StatesmanHealthCheck(registry);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(1, result.Data["statesman.load.reports"]);
        Assert.Equal(1, result.Data["statesman.load.reports.incomplete"]);
        Assert.Equal(1L, result.Data["statesman.load.sources.faulted"]);
    }

    [Fact]
    public async Task Healthy_again_once_that_address_loads_completely()
    {
        // The self-correction the status rule depends on: reports are latest-per-address, so no drain
        // call is needed for a transient loader failure to stop degrading the check.
        var clock = new ManualTimeProvider();
        var source = new FlakySource(clock);
        await using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(source);
            services.AddStatesman(BuildLoadingDeclaration("load-recovers"));
        });
        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
        IStatesman root = registry.Get("load-recovers");
        await root.InitializeAsync();
        source.Fail = true;
        _ = await root.State(Loaded).RefreshAsync();
        var check = new StatesmanHealthCheck(registry);
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        source.Fail = false;
        _ = await root.State(Loaded).RefreshAsync();

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0, result.Data["statesman.load.reports.incomplete"]);
    }

    [Fact]
    public async Task The_slowest_source_is_named_with_its_exact_duration()
    {
        var clock = new ManualTimeProvider();
        var source = new FlakySource(clock);
        await using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(source);
            services.AddStatesman(BuildLoadingDeclaration("load-slowest"));
        });
        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
        IStatesman root = registry.Get("load-slowest");
        await root.InitializeAsync();
        _ = await root.State(Loaded).RefreshAsync();

        var check = new StatesmanHealthCheck(registry);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("upstream", result.Data["statesman.load.slowest.source"]);
        Assert.Equal(250d, result.Data["statesman.load.slowest.duration.ms"]);
    }

    private static StatesmanDeclaration BuildLoadingDeclaration(string rootId) =>
        global::Statesman.Statesman.Declare(rootId)
            .State(Loaded, state => state
                .Initial(0)
                .Load(load => load
                    .From<FlakySource, int>("upstream", (service, _, cancellationToken) =>
                        service.FetchAsync(cancellationToken))
                    .Into((_, value, _) => value)
                    .BestEffort()))
            .Build();

    /// <summary>
    /// A source that always takes exactly 250 virtual milliseconds and throws when <see cref="Fail"/>
    /// is set, so one runtime produces a complete load and a partial one on demand.
    /// </summary>
    private sealed class FlakySource
    {
        private readonly ManualTimeProvider _clock;

        public FlakySource(ManualTimeProvider clock) => _clock = clock;

        public bool Fail { get; set; }

        public ValueTask<int> FetchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _clock.Advance(TimeSpan.FromMilliseconds(250));
            return Fail ? throw new TimeoutException("upstream timed out") : ValueTask.FromResult(1);
        }
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
}
