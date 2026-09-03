using Statesman.Testing;

namespace Statesman.Tests;

public sealed class RuntimeMaintenanceLeaseTests
{
    private static readonly StateKey<int> Counter = StateKey.Define<int>("maintenance/counter");
    private static readonly StateKey<int> DeniedCounter = StateKey.Define<int>("maintenance-groups/denied");
    private static readonly StateKey<int> DegradedCounter = StateKey.Define<int>("maintenance-groups/degraded");

    [Fact]
    public async Task MaintainAsync_runs_refresh_when_the_store_grants_a_lease()
    {
        var clock = new ManualTimeProvider();
        var api = new CounterApi();
        var lease = new FakeLease();
        var store = new FakeLeaseStore(
            new InMemoryStateLedgerStore("leased", clock),
            () => ValueTask.FromResult<IStateLease?>(lease));

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration(),
            services => services.Add<ICounterApi>(api),
            stores: new IStateLedgerStore[] { store },
            time: clock);
        harness.Runtime.State(Counter);

        await harness.Runtime.MaintainAsync();

        Assert.Equal(1, api.CallCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task MaintainAsync_skips_refresh_when_the_store_denies_the_lease()
    {
        var clock = new ManualTimeProvider();
        var api = new CounterApi();
        var store = new FakeLeaseStore(
            new InMemoryStateLedgerStore("leased", clock),
            () => ValueTask.FromResult<IStateLease?>(null));

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration(),
            services => services.Add<ICounterApi>(api),
            stores: new IStateLedgerStore[] { store },
            time: clock);
        harness.Runtime.State(Counter);

        await harness.Runtime.MaintainAsync();

        Assert.Equal(0, api.CallCount);
    }

    [Fact]
    public async Task MaintainAsync_runs_refresh_when_the_store_has_no_lease_provider()
    {
        var clock = new ManualTimeProvider();
        var api = new CounterApi();
        var store = new InMemoryStateLedgerStore("leased", clock);

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration(),
            services => services.Add<ICounterApi>(api),
            stores: new IStateLedgerStore[] { store },
            time: clock);
        harness.Runtime.State(Counter);

        await harness.Runtime.MaintainAsync();

        Assert.Equal(1, api.CallCount);
    }

    [Fact]
    public async Task MaintainAsync_holds_the_lease_while_refresh_is_in_flight()
    {
        var clock = new ManualTimeProvider();
        var lease = new FakeLease();
        var api = new LeaseObservingApi(() => lease.IsDisposed);
        var store = new FakeLeaseStore(
            new InMemoryStateLedgerStore("leased", clock),
            () => ValueTask.FromResult<IStateLease?>(lease));

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            BuildDeclaration(),
            services => services.Add<ICounterApi>(api),
            stores: new IStateLedgerStore[] { store },
            time: clock);
        harness.Runtime.State(Counter);

        await harness.Runtime.MaintainAsync();

        Assert.Equal(1, api.CallCount);
        Assert.False(api.ObservedLeaseDisposedDuringLoad);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task MaintainAsync_treats_store_groups_independently()
    {
        var clock = new ManualTimeProvider();
        var deniedApi = new DeniedApi();
        var degradedApi = new DegradedApi();
        var deniedStore = new FakeLeaseStore(
            new InMemoryStateLedgerStore("denied-store", clock),
            () => ValueTask.FromResult<IStateLease?>(null));
        var degradedStore = new InMemoryStateLedgerStore("degraded-store", clock);

        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("maintenance-groups")
            .State(DeniedCounter, state => state
                .StoreWith("denied-store")
                .Initial(0)
                .Refresh(refresh => refresh.Every(TimeSpan.FromMinutes(1)))
                .Load(load => load
                    .From<IDeniedApi, int>(
                        "value",
                        (service, context, cancellationToken) => service.GetValueAsync(cancellationToken))
                    .Into((current, value, _) => value)))
            .State(DegradedCounter, state => state
                .StoreWith("degraded-store")
                .Initial(0)
                .Refresh(refresh => refresh.Every(TimeSpan.FromMinutes(1)))
                .Load(load => load
                    .From<IDegradedApi, int>(
                        "value",
                        (service, context, cancellationToken) => service.GetValueAsync(cancellationToken))
                    .Into((current, value, _) => value)))
            .Build();

        await using StatesmanTestHarness harness = StatesmanTestHarness.Create(
            declaration,
            services => services.Add<IDeniedApi>(deniedApi).Add<IDegradedApi>(degradedApi),
            stores: new IStateLedgerStore[] { deniedStore, degradedStore },
            time: clock);
        harness.Runtime.State(DeniedCounter);
        harness.Runtime.State(DegradedCounter);

        await harness.Runtime.MaintainAsync();

        Assert.Equal(0, deniedApi.CallCount);
        Assert.Equal(1, degradedApi.CallCount);
    }

    private static StatesmanDeclaration BuildDeclaration() =>
        global::Statesman.Statesman.Declare("maintenance")
            .State(Counter, state => state
                .StoreWith("leased")
                .Initial(0)
                .Refresh(refresh => refresh.Every(TimeSpan.FromMinutes(1)))
                .Load(load => load
                    .From<ICounterApi, int>(
                        "value",
                        (service, context, cancellationToken) => service.GetValueAsync(cancellationToken))
                    .Into((current, value, _) => value)))
            .Build();

    public interface ICounterApi
    {
        ValueTask<int> GetValueAsync(CancellationToken cancellationToken);
    }

    private interface IDeniedApi
    {
        ValueTask<int> GetValueAsync(CancellationToken cancellationToken);
    }

    private interface IDegradedApi
    {
        ValueTask<int> GetValueAsync(CancellationToken cancellationToken);
    }

    private sealed class CounterApi : ICounterApi
    {
        public int CallCount { get; private set; }

        public ValueTask<int> GetValueAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(CallCount);
        }
    }

    private sealed class LeaseObservingApi : ICounterApi
    {
        private readonly Func<bool> _isLeaseDisposed;

        public LeaseObservingApi(Func<bool> isLeaseDisposed)
        {
            _isLeaseDisposed = isLeaseDisposed;
        }

        public int CallCount { get; private set; }

        public bool ObservedLeaseDisposedDuringLoad { get; private set; }

        public ValueTask<int> GetValueAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            ObservedLeaseDisposedDuringLoad = _isLeaseDisposed();
            return ValueTask.FromResult(CallCount);
        }
    }

    private sealed class DeniedApi : IDeniedApi
    {
        public int CallCount { get; private set; }

        public ValueTask<int> GetValueAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(CallCount);
        }
    }

    private sealed class DegradedApi : IDegradedApi
    {
        public int CallCount { get; private set; }

        public ValueTask<int> GetValueAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(CallCount);
        }
    }

    private sealed class FakeLease : IStateLease
    {
        public int DisposeCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLeaseStore : IStateLedgerStore, IStateLeaseProvider
    {
        private readonly InMemoryStateLedgerStore _inner;
        private readonly Func<ValueTask<IStateLease?>> _acquire;

        public FakeLeaseStore(InMemoryStateLedgerStore inner, Func<ValueTask<IStateLease?>> acquire)
        {
            _inner = inner;
            _acquire = acquire;
        }

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
            _inner.PruneAsync(address, policy, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        public ValueTask<IStateLease?> AcquireAsync(
            string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            _acquire();
    }
}
