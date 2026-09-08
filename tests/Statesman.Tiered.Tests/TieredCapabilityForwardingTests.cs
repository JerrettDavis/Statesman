namespace Statesman.Tiered.Tests;

public sealed class TieredCapabilityForwardingTests
{
    [Fact]
    public void TryGetCapability_forwards_to_the_hot_store_when_it_implements_the_capability()
    {
        var hot = new FakeLeaseStore(new InMemoryStateLedgerStore("hot"));
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        bool found = tiered.TryGetCapability(out IStateLeaseProvider? leases);

        Assert.True(found);
        Assert.Same(hot, leases);
    }

    [Fact]
    public void TryGetCapability_forwards_to_the_cold_store_when_only_it_implements_the_capability()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new FakeLeaseStore(new InMemoryStateLedgerStore("cold"));
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        bool found = tiered.TryGetCapability(out IStateLeaseProvider? leases);

        Assert.True(found);
        Assert.Same(cold, leases);
    }

    [Fact]
    public void TryGetCapability_returns_false_when_neither_tier_implements_the_capability()
    {
        var tiered = new TieredStateLedgerStore(
            "tiered", new InMemoryStateLedgerStore("hot"), new InMemoryStateLedgerStore("cold"));

        bool found = tiered.TryGetCapability(out IStateLeaseProvider? leases);

        Assert.False(found);
        Assert.Null(leases);
    }

    [Fact]
    public void TryGetCapability_never_forwards_IStateLedgerReplica_even_when_both_tiers_implement_it()
    {
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        Assert.True(hot.TryGetCapability(out IStateLedgerReplica? _));
        Assert.True(cold.TryGetCapability(out IStateLedgerReplica? _));

        bool found = tiered.TryGetCapability(out IStateLedgerReplica? replica);

        Assert.False(found);
        Assert.Null(replica);
    }

    [Fact]
    public void TryGetCapability_by_type_answers_with_this_store_for_IStateChangeNotifier()
    {
        // The type-based overload is public and the generic forwarder inside it tries HOT first, so
        // before the guard it handed out the hot tier's notifier -- hints for a feed no consumer of
        // a tiered store ever reads. StateCapabilityExtensions.TryGetCapability<T> casts first and
        // was always safe; this pins the overload it is built on to the same answer.
        var hot = new InMemoryStateLedgerStore("hot");
        var cold = new InMemoryStateLedgerStore("cold");
        var tiered = new TieredStateLedgerStore("tiered", hot, cold);

        Assert.True(hot.TryGetCapability(out IStateChangeNotifier? hotNotifier));
        Assert.True(cold.TryGetCapability(out IStateChangeNotifier? _));

        bool found = ((IStateCapabilityProvider)tiered).TryGetCapability(
            typeof(IStateChangeNotifier), out object? capability);

        Assert.True(found);
        Assert.Same(tiered, capability);
        Assert.NotSame(hotNotifier, capability);
    }

    private sealed class FakeLeaseStore : IStateLedgerStore, IStateLedgerReplica, IStateLeaseProvider
    {
        private readonly InMemoryStateLedgerStore _inner;

        public FakeLeaseStore(InMemoryStateLedgerStore inner) => _inner = inner;

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

        public ValueTask ImportAsync(StateRecord record, CancellationToken cancellationToken = default) =>
            _inner.ImportAsync(record, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        public ValueTask<IStateLease?> AcquireAsync(
            string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }
}
