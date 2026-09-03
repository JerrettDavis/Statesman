using System.Runtime.CompilerServices;
using Statesman.Testing;

namespace Statesman.Tests;

public sealed class RuntimeMaintenanceLeaseTests
{
    private static readonly StateKey<int> Counter = StateKey.Define<int>("maintenance/counter");

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

        Assert.True(api.CallCount >= 1);
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

        Assert.True(api.CallCount >= 1);
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

    private sealed class CounterApi : ICounterApi
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

        public ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
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
